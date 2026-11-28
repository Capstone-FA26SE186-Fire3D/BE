-- BE#242: thẩm định items are assigned round-robin instead of one shared queue where the slower expert lost the work.
-- * Each submission (scenario content review, fire infrastructure version) is assigned to ONE available FireSafetyExpert in turn
--   (A, B, C, A, ...): active, not deleted, email verified and past the first-login email step. A resubmission is a new item.
-- * No expert available: the item waits unassigned and is handed out on the next expert/admin call or submission.
-- * Only the assignee decides a waiting item (403 REVIEW_NOT_ASSIGNED_TO_YOU for the others, who still read it). Decided items keep
--   their 412/409 answers. GET /api/expert/reviews filters assignee=me (default) | all and returns assigneeId/assigneeName/assignedAt.
-- * PlatformAdmin lists waiting items with their assignee and reassigns one (reason 1-500 characters, audited) through
--   expert_review_admin_gate.
-- The gate bodies are the ones from 20261124110000_FireInfrastructure with these additions.
CREATE TEMP TABLE expert_assignment_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO expert_assignment_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE TABLE IF NOT EXISTS public.expert_review_assignments(
 review_kind text NOT NULL CHECK(review_kind IN('Scenario','Infrastructure')),
 review_id uuid NOT NULL,
 assignee_id uuid NOT NULL REFERENCES public.users(id) ON DELETE RESTRICT,
 assigned_at timestamptz NOT NULL,
 source text NOT NULL CHECK(source IN('RoundRobin','Reassigned')),
 assigned_by uuid REFERENCES public.users(id) ON DELETE RESTRICT,
 reason text CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 500),
 PRIMARY KEY(review_kind,review_id),
 CHECK((source='RoundRobin' AND assigned_by IS NULL AND reason IS NULL) OR (source='Reassigned' AND assigned_by IS NOT NULL AND reason IS NOT NULL)));
CREATE INDEX IF NOT EXISTS expert_review_assignments_assignee ON public.expert_review_assignments(assignee_id);
-- One row: the expert who got the last item, so the next item goes to the expert after them.
CREATE TABLE IF NOT EXISTS public.expert_review_rotation(
 id boolean PRIMARY KEY DEFAULT true CHECK(id),
 last_expert_id uuid REFERENCES public.users(id) ON DELETE SET NULL);
INSERT INTO public.expert_review_rotation(id) VALUES(true) ON CONFLICT DO NOTHING;

ALTER TABLE public.expert_review_assignments ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.expert_review_rotation ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.expert_review_assignments, public.expert_review_rotation FROM PUBLIC;
DO $$
DECLARE target_table text; target_role text;
BEGIN
 FOREACH target_table IN ARRAY ARRAY['expert_review_assignments','expert_review_rotation'] LOOP
  EXECUTE format('GRANT SELECT,INSERT,UPDATE ON public.%I TO fet3d_ifc_upload_owner', target_table);
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=target_table AND policyname='expert_assignment_owner') THEN
   EXECUTE format('CREATE POLICY expert_assignment_owner ON public.%I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)', target_table);
  END IF;
  -- cleanup_pending_registrations deletes users: the foreign-key checks of tables that reference users need read access (#235 lesson).
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
   EXECUTE format('GRANT SELECT,UPDATE ON public.%I TO fet3d_pending_cleanup_owner', target_table);
   IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=target_table AND policyname='pending_cleanup_owner') THEN
    EXECUTE format('CREATE POLICY pending_cleanup_owner ON public.%I TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true)', target_table);
   END IF;
  END IF;
  FOREACH target_role IN ARRAY ARRAY['anon','authenticated'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=target_role) THEN EXECUTE format('REVOKE ALL ON public.%I FROM %I', target_table, target_role); END IF;
  END LOOP;
 END LOOP;
END $$;

-- An expert who can receive work: active, not deleted, email verified and past the first-login email step (BE#235).
CREATE OR REPLACE FUNCTION expert_is_available(p_user uuid) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT EXISTS(SELECT 1 FROM users u WHERE u.id=p_user AND u.role::text='FireSafetyExpert' AND u.is_active AND u.deleted_at IS NULL
  AND u.email_verified_at IS NOT NULL AND u.first_login_step IS DISTINCT FROM 'VerifyEmail')
$$;

-- The next available expert after the last one served, ordered by account creation (then id); wraps around. NULL when none.
CREATE OR REPLACE FUNCTION expert_next_assignee() RETURNS uuid LANGUAGE plpgsql SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE last_id uuid;last_created timestamptz;next_id uuid;
BEGIN
 SELECT last_expert_id INTO last_id FROM expert_review_rotation WHERE id FOR UPDATE;
 SELECT created_at INTO last_created FROM users WHERE id=last_id;
 IF last_created IS NOT NULL THEN
  SELECT u.id INTO next_id FROM users u WHERE u.role::text='FireSafetyExpert' AND expert_is_available(u.id) AND (u.created_at,u.id)>(last_created,last_id)
   ORDER BY u.created_at,u.id LIMIT 1;
 END IF;
 IF next_id IS NULL THEN
  SELECT u.id INTO next_id FROM users u WHERE u.role::text='FireSafetyExpert' AND expert_is_available(u.id) ORDER BY u.created_at,u.id LIMIT 1;
 END IF;
 IF next_id IS NOT NULL THEN UPDATE expert_review_rotation SET last_expert_id=next_id WHERE id; END IF;
 RETURN next_id;
END $$;

-- Hands every waiting, unassigned item to the next expert in turn, oldest submission first. Returns how many were assigned.
CREATE OR REPLACE FUNCTION expert_assign_pending() RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE item record;who uuid;n integer:=0;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM scenario_content_reviews r WHERE r.status='Submitted'
   AND NOT EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Scenario' AND a.review_id=r.id))
  AND NOT EXISTS(SELECT 1 FROM fire_infrastructures f WHERE f.status='Submitted'
   AND NOT EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Infrastructure' AND a.review_id=f.id)) THEN RETURN 0; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:expert-assignment',0));
 FOR item IN
  SELECT 'Scenario' AS kind,r.id,r.submitted_at FROM scenario_content_reviews r WHERE r.status='Submitted'
   AND NOT EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Scenario' AND a.review_id=r.id)
  UNION ALL
  SELECT 'Infrastructure',f.id,f.submitted_at FROM fire_infrastructures f WHERE f.status='Submitted'
   AND NOT EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Infrastructure' AND a.review_id=f.id)
  ORDER BY 3,2
 LOOP
  who:=expert_next_assignee();
  EXIT WHEN who IS NULL;
  INSERT INTO expert_review_assignments(review_kind,review_id,assignee_id,assigned_at,source) VALUES(item.kind,item.id,who,clock_timestamp(),'RoundRobin')
   ON CONFLICT (review_kind,review_id) DO NOTHING;
  n:=n+1;
 END LOOP;
 RETURN n;
END $$;

-- A submission is assigned in the same transaction that submits it.
CREATE OR REPLACE FUNCTION expert_review_assign_on_submit() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
BEGIN
 PERFORM expert_assign_pending();
 RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS scenario_content_review_assign ON scenario_content_reviews;
CREATE TRIGGER scenario_content_review_assign AFTER INSERT ON scenario_content_reviews FOR EACH ROW WHEN (NEW.status='Submitted')
 EXECUTE FUNCTION expert_review_assign_on_submit();
DROP TRIGGER IF EXISTS fire_infrastructure_assign ON fire_infrastructures;
CREATE TRIGGER fire_infrastructure_assign AFTER UPDATE OF status ON fire_infrastructures FOR EACH ROW
 WHEN (NEW.status='Submitted' AND OLD.status IS DISTINCT FROM 'Submitted') EXECUTE FUNCTION expert_review_assign_on_submit();

CREATE OR REPLACE FUNCTION expert_review_assignment(p_kind text,p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT jsonb_build_object('assigneeId',a.assignee_id,'assigneeName',u.full_name,'assignedAt',a.assigned_at)
 FROM (SELECT p_kind AS k,p_id AS i) x LEFT JOIN expert_review_assignments a ON a.review_kind=x.k AND a.review_id=x.i LEFT JOIN users u ON u.id=a.assignee_id
$$;

CREATE OR REPLACE FUNCTION expert_review_item(p_review uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT scenario_review_summary(r.id)||jsonb_build_object('kind','Scenario','revision',CASE WHEN r.status='Submitted' THEN 1 ELSE 2 END,'revisionId',v.revision_id)
  ||expert_review_assignment('Scenario',r.id)
 FROM scenario_content_reviews r JOIN scenario_versions v ON v.id=r.scenario_version_id WHERE r.id=p_review
$$;

CREATE OR REPLACE FUNCTION expert_infrastructure_item(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT jsonb_build_object('reviewId',f.id,'kind','Infrastructure','status',f.status,'revision',f.row_revision,'infrastructureId',f.id,'revisionId',f.revision_id,
  'contentHash',f.content_hash,'submittedBy',f.submitted_by,'submittedByName',su.full_name,'submittedAt',f.submitted_at,
  'decidedBy',f.decided_by,'decidedByName',du.full_name,'decidedAt',f.decided_at,'reason',f.decision_reason,
  'organizationId',f.organization_id,'organizationName',o.name,'buildingId',f.building_id,'buildingName',b.name,'versionNumber',f.version_number)||expert_review_assignment('Infrastructure',f.id)
 FROM fire_infrastructures f JOIN buildings b ON b.id=f.building_id JOIN organizations o ON o.id=f.organization_id
 LEFT JOIN users su ON su.id=f.submitted_by LEFT JOIN users du ON du.id=f.decided_by WHERE f.id=p_id
$$;

CREATE OR REPLACE FUNCTION expert_review_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;r scenario_content_reviews;v scenario_versions;b buildings;s scenarios;f fire_infrastructures;receipt readiness_command_receipts;
 requested_kind text;requested_status text;requested_assignee text;page_number integer;page_size integer;total integer;result jsonb;h text;before_values jsonb;reason_text text;stamp timestamptz;
BEGIN
 IF p_action NOT IN('List','Get','Preview','Approve','Reject') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR p_family IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 -- Only experts; PlatformAdmin has no thẩm định path, not even while no expert exists (FR-PLAN-05).
 IF actor.role::text<>'FireSafetyExpert' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 -- BE#242: items that waited while no expert was available are handed out before anything is read or decided.
 PERFORM expert_assign_pending();

 IF p_action='List' THEN
  requested_kind:=p_input->>'kind';requested_status:=COALESCE(p_input->>'status','Submitted');requested_assignee:=COALESCE(p_input->>'assignee','me');
  page_number:=COALESCE((p_input->>'page')::int,1);page_size:=COALESCE((p_input->>'pageSize')::int,20);
  IF (requested_kind IS NOT NULL AND requested_kind NOT IN('Scenario','Infrastructure')) OR requested_status NOT IN('Submitted','Approved','Rejected')
   OR requested_assignee NOT IN('me','all')
   OR page_number<1 OR page_size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  -- BE#242: assignee=me (default) lists the items assigned to the caller; assignee=all lists every item (others' are read-only).
  -- Waiting items oldest first (first come, first reviewed); decided items newest decision first.
  WITH q AS (
   SELECT 'Scenario' AS kind,id,submitted_at,reviewed_at AS decided_at FROM scenario_content_reviews
    WHERE status=requested_status AND (requested_kind IS NULL OR requested_kind='Scenario')
     AND (requested_assignee='all' OR EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Scenario' AND a.review_id=scenario_content_reviews.id AND a.assignee_id=p_actor))
   UNION ALL
   SELECT 'Infrastructure',id,submitted_at,decided_at FROM fire_infrastructures
    WHERE status=requested_status AND (requested_kind IS NULL OR requested_kind='Infrastructure')
     AND (requested_assignee='all' OR EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Infrastructure' AND a.review_id=fire_infrastructures.id AND a.assignee_id=p_actor))),
  paged AS (
   SELECT kind,id,row_number() OVER(ORDER BY CASE WHEN requested_status='Submitted' THEN submitted_at END,
    CASE WHEN requested_status<>'Submitted' THEN decided_at END DESC,id) rn FROM q)
  SELECT (SELECT count(*) FROM q),
   COALESCE((SELECT jsonb_agg(CASE WHEN p.kind='Scenario' THEN expert_review_item(p.id) ELSE expert_infrastructure_item(p.id) END ORDER BY p.rn)
    FROM paged p WHERE p.rn>(page_number::bigint-1)*page_size AND p.rn<=page_number::bigint*page_size),'[]'::jsonb)
  INTO total,result;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'totalCount',total,'page',page_number,'pageSize',page_size));
 END IF;

 SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource;
 IF r.id IS NULL THEN
  -- Infrastructure versions enter the queue when submitted; a Draft is the organization's own work and stays invisible.
  SELECT * INTO f FROM fire_infrastructures WHERE id=p_resource AND status<>'Draft';
  SELECT * INTO b FROM buildings WHERE id=f.building_id;
  IF f.id IS NULL OR b.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF p_action='Get' THEN
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('review',expert_infrastructure_item(f.id),
    'content',jsonb_build_object('geometry',f.geometry,'runtimeVersion',f.runtime_version,'items',f.items),'rubric',NULL,'learningObjectives',NULL,'learnerInstructions',NULL));
  ELSIF p_action='Preview' THEN
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('buildingId',b.id,'revisionId',f.revision_id));
  END IF;
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('review',p_resource,'input',p_input,'expected',p_expected));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:expert-review:'||p_actor||':'||p_action||':'||p_key,0));
  SELECT * INTO receipt FROM readiness_command_receipts WHERE actor_id=p_actor AND operation='Expert'||p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   RETURN receipt.result;
  END IF;
  IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
  reason_text:=NULLIF(btrim(p_input->>'reason'),'');
  IF p_action='Reject' AND (reason_text IS NULL OR length(reason_text)>2000) THEN
   RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('A rejection reason of 1-2000 characters is required.')));END IF;
  IF p_action='Approve' THEN reason_text:=NULL;END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
  SELECT * INTO f FROM fire_infrastructures WHERE id=p_resource FOR UPDATE;
  -- BE#242: only the assigned expert decides a waiting item; the others read it.
  IF f.status='Submitted' AND NOT EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Infrastructure' AND a.review_id=f.id AND a.assignee_id=p_actor) THEN
   RETURN jsonb_build_object('code','REVIEW_NOT_ASSIGNED_TO_YOU','status',403);END IF;
  IF p_expected<>f.row_revision THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
  IF f.status<>'Submitted' THEN RETURN jsonb_build_object('code','FIRE_INFRASTRUCTURE_NOT_PENDING','status',409);END IF;
  IF f.content_hash IS DISTINCT FROM fire_infrastructure_content_hash(f) THEN RETURN jsonb_build_object('code','CONTENT_HASH_MISMATCH','status',409);END IF;
  stamp:=clock_timestamp();
  UPDATE fire_infrastructures SET status=CASE WHEN p_action='Approve' THEN 'Approved' ELSE 'Rejected' END,decided_by=p_actor,decided_at=stamp,
   decision_reason=reason_text,row_revision=row_revision+1,updated_at=stamp WHERE id=f.id RETURNING * INTO f;
  INSERT INTO organization_notifications(id,organization_id,recipient_user_id,building_id,fire_infrastructure_id,notification_type,title,body,idempotency_key,created_at)
  SELECT gen_random_uuid(),b.organization_id,u.id,b.id,f.id,CASE WHEN f.status='Approved' THEN 'FireInfrastructureApproved' ELSE 'FireInfrastructureRejected' END,
   CASE WHEN f.status='Approved' THEN 'Hạ tầng PCCC đạt thẩm định' ELSE 'Hạ tầng PCCC chưa đạt thẩm định' END,
   'Hạ tầng PCCC (phiên bản '||f.version_number||') của công trình '||b.name||
   CASE WHEN f.status='Approved' THEN ' đã đạt thẩm định của Chuyên viên PCCC và đã được khóa.' ELSE ' chưa đạt thẩm định của Chuyên viên PCCC. Lý do: '||f.decision_reason END,
   'fire-infrastructure:'||f.id,stamp
  FROM users u WHERE u.id=f.submitted_by AND u.role='OrganizationUser' AND u.organization_id=b.organization_id AND u.is_active AND u.deleted_at IS NULL
  ON CONFLICT (idempotency_key) DO NOTHING;
  result:=jsonb_build_object('code','OK','result',expert_infrastructure_item(f.id));
  INSERT INTO readiness_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Expert'||p_action,p_key,h,result);
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User',CASE WHEN p_action='Reject' THEN 'Reject'::audit_action_enum ELSE 'Update'::audit_action_enum END,
   'FireInfrastructure',f.id,gen_random_uuid(),jsonb_build_object('status','Submitted'),
   jsonb_build_object('auditContractVersion',1,'status',f.status,'version',f.version_number,'contentHash',f.content_hash,'submittedBy',f.submitted_by,
    'decidedBy',f.decided_by,'decidedAt',f.decided_at,'decidedByRole','FireSafetyExpert'),now());
  RETURN result;
 END IF;

 SELECT * INTO v FROM scenario_versions WHERE id=r.scenario_version_id;
 SELECT * INTO b FROM buildings WHERE id=v.building_id;
 IF r.id IS NULL OR v.id IS NULL OR b.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;

 IF p_action='Get' THEN
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('review',expert_review_item(r.id),'content',v.state_snapshot,'rubric',v.rubric,
   'learningObjectives',v.learning_objectives,'learnerInstructions',v.learner_instructions));
 ELSIF p_action='Preview' THEN
  -- The caller signs the 3D preview of exactly this revision; nothing else of the organization is opened.
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('buildingId',b.id,'revisionId',v.revision_id));
 END IF;

 -- Approve / Reject
 IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 h:=fet3d_jsonb_payload_hash(jsonb_build_object('review',p_resource,'input',p_input,'expected',p_expected));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:expert-review:'||p_actor||':'||p_action||':'||p_key,0));
 SELECT * INTO receipt FROM readiness_command_receipts WHERE actor_id=p_actor AND operation='Expert'||p_action AND idempotency_key=p_key;
 IF receipt.result IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
  RETURN receipt.result;
 END IF;
 IF p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);END IF;
 reason_text:=NULLIF(btrim(p_input->>'reason'),'');
 IF p_action='Reject' AND (reason_text IS NULL OR length(reason_text)>2000) THEN
  RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('A rejection reason of 1-2000 characters is required.')));END IF;
 IF p_action='Approve' THEN reason_text:=NULL;END IF;
 -- Same lock order as scenario_readiness_gate: Building, then the review row.
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource FOR UPDATE;
 IF r.status='Submitted' AND NOT EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Scenario' AND a.review_id=r.id AND a.assignee_id=p_actor) THEN
  RETURN jsonb_build_object('code','REVIEW_NOT_ASSIGNED_TO_YOU','status',403);END IF;
 IF p_expected<>(CASE WHEN r.status='Submitted' THEN 1 ELSE 2 END) THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
 IF r.status<>'Submitted' THEN RETURN jsonb_build_object('code','CONTENT_REVIEW_NOT_PENDING','status',409);END IF;
 IF r.content_hash<>v.scenario_hash OR r.rubric_hash<>fet3d_jsonb_payload_hash(v.rubric) THEN RETURN jsonb_build_object('code','CONTENT_HASH_MISMATCH','status',409);END IF;
 before_values:=jsonb_build_object('status',r.status,'reviewId',r.id,'scenarioVersionId',v.id,'submittedBy',r.submitted_by,'submittedAt',r.submitted_at,'reviewedBy',r.reviewed_by,'reviewedAt',r.reviewed_at);
 stamp:=clock_timestamp();
 UPDATE scenario_content_reviews SET status=CASE WHEN p_action='Approve' THEN 'Approved' ELSE 'Rejected' END,reviewed_by=p_actor,reviewed_at=stamp,reason=reason_text
  WHERE id=r.id RETURNING * INTO r;
 SELECT * INTO s FROM scenarios WHERE id=v.scenario_id;
 -- The submitter hears the result while they are still an active OrganizationUser of that organization.
 INSERT INTO organization_notifications(id,organization_id,recipient_user_id,building_id,content_review_id,scenario_version_id,notification_type,title,body,idempotency_key,created_at)
 SELECT gen_random_uuid(),b.organization_id,u.id,b.id,r.id,v.id,CASE WHEN r.status='Approved' THEN 'ContentReviewApproved' ELSE 'ContentReviewRejected' END,
  CASE WHEN r.status='Approved' THEN 'Tình huống đạt thẩm định' ELSE 'Tình huống chưa đạt thẩm định' END,
  'Tình huống "'||COALESCE(s.name,'')||'" (phiên bản '||v.version_number||') của công trình '||b.name||
  CASE WHEN r.status='Approved' THEN ' đã đạt thẩm định của Chuyên viên PCCC.' ELSE ' chưa đạt thẩm định của Chuyên viên PCCC. Lý do: '||r.reason END,
  'content-review:'||r.id,stamp
 FROM users u WHERE u.id=r.submitted_by AND u.role='OrganizationUser' AND u.organization_id=b.organization_id AND u.is_active AND u.deleted_at IS NULL
 ON CONFLICT (idempotency_key) DO NOTHING;
 result:=jsonb_build_object('code','OK','result',expert_review_item(r.id));
 INSERT INTO readiness_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,'Expert'||p_action,p_key,h,result);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,b.organization_id,'User',CASE WHEN p_action='Reject' THEN 'Reject'::audit_action_enum ELSE 'Update'::audit_action_enum END,
  'ScenarioVersion',v.id,gen_random_uuid(),before_values,
  jsonb_build_object('auditContractVersion',1,'status',r.status,'reviewId',r.id,'scenarioVersionId',v.id,'submittedBy',r.submitted_by,'submittedAt',r.submitted_at,
   'reviewedBy',r.reviewed_by,'reviewedAt',r.reviewed_at,'decidedByRole','FireSafetyExpert'),now());
 RETURN result;
END $$;

-- PlatformAdmin: List waiting (or decided) items with their assignee; Reassign one Submitted item to another available expert.
CREATE OR REPLACE FUNCTION expert_review_admin_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;target users;r scenario_content_reviews;v scenario_versions;f fire_infrastructures;b buildings;kind text;current_assignee uuid;
 requested_kind text;requested_status text;requested_assignee uuid;page_number integer;page_size integer;total integer;result jsonb;reason_text text;stamp timestamptz;
BEGIN
 IF p_action NOT IN('List','Reassign') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR p_family IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role::text<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 PERFORM expert_assign_pending();

 IF p_action='List' THEN
  requested_kind:=p_input->>'kind';requested_status:=COALESCE(p_input->>'status','Submitted');requested_assignee:=(p_input->>'assigneeId')::uuid;
  page_number:=COALESCE((p_input->>'page')::int,1);page_size:=COALESCE((p_input->>'pageSize')::int,20);
  IF (requested_kind IS NOT NULL AND requested_kind NOT IN('Scenario','Infrastructure')) OR requested_status NOT IN('Submitted','Approved','Rejected')
   OR page_number<1 OR page_size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  WITH q AS (
   SELECT 'Scenario' AS kind,s.id,s.submitted_at,s.reviewed_at AS decided_at FROM scenario_content_reviews s
    WHERE s.status=requested_status AND (requested_kind IS NULL OR requested_kind='Scenario')
     AND (requested_assignee IS NULL OR EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Scenario' AND a.review_id=s.id AND a.assignee_id=requested_assignee))
   UNION ALL
   SELECT 'Infrastructure',i.id,i.submitted_at,i.decided_at FROM fire_infrastructures i
    WHERE i.status=requested_status AND (requested_kind IS NULL OR requested_kind='Infrastructure')
     AND (requested_assignee IS NULL OR EXISTS(SELECT 1 FROM expert_review_assignments a WHERE a.review_kind='Infrastructure' AND a.review_id=i.id AND a.assignee_id=requested_assignee))),
  paged AS (
   SELECT q.kind,q.id,row_number() OVER(ORDER BY CASE WHEN requested_status='Submitted' THEN q.submitted_at END,
    CASE WHEN requested_status<>'Submitted' THEN q.decided_at END DESC,q.id) rn FROM q)
  SELECT (SELECT count(*) FROM q),
   COALESCE((SELECT jsonb_agg(CASE WHEN p.kind='Scenario' THEN expert_review_item(p.id) ELSE expert_infrastructure_item(p.id) END ORDER BY p.rn)
    FROM paged p WHERE p.rn>(page_number::bigint-1)*page_size AND p.rn<=page_number::bigint*page_size),'[]'::jsonb)
  INTO total,result;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'totalCount',total,'page',page_number,'pageSize',page_size));
 END IF;

 -- Reassign
 reason_text:=NULLIF(btrim(p_input->>'reason'),'');
 IF reason_text IS NULL OR length(reason_text)>500 THEN
  RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('A reason of 1-500 characters is required.')));END IF;
 SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource;
 IF r.id IS NOT NULL THEN
  kind:='Scenario';SELECT * INTO v FROM scenario_versions WHERE id=r.scenario_version_id;SELECT * INTO b FROM buildings WHERE id=v.building_id;
 ELSE
  SELECT * INTO f FROM fire_infrastructures WHERE id=p_resource AND status<>'Draft';
  IF f.id IS NOT NULL THEN kind:='Infrastructure';SELECT * INTO b FROM buildings WHERE id=f.building_id;END IF;
 END IF;
 IF kind IS NULL OR b.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 -- Same lock order as a decision (Building, then the item), so a reassignment never races a decision.
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF kind='Scenario' THEN SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource FOR UPDATE;
  IF r.status<>'Submitted' THEN RETURN jsonb_build_object('code','REVIEW_NOT_PENDING','status',409);END IF;
 ELSE SELECT * INTO f FROM fire_infrastructures WHERE id=p_resource FOR UPDATE;
  IF f.status<>'Submitted' THEN RETURN jsonb_build_object('code','REVIEW_NOT_PENDING','status',409);END IF;
 END IF;
 SELECT * INTO target FROM users WHERE id=(p_input->>'expertId')::uuid;
 IF target.id IS NULL OR NOT expert_is_available(target.id) THEN
  RETURN jsonb_build_object('code','EXPERT_NOT_AVAILABLE','status',422,'errors',jsonb_build_object('expertId',jsonb_build_array('Choose an active FireSafetyExpert whose email is verified.')));END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:expert-assignment',0));
 SELECT assignee_id INTO current_assignee FROM expert_review_assignments WHERE review_kind=kind AND review_id=p_resource FOR UPDATE;
 IF current_assignee IS DISTINCT FROM target.id THEN
  stamp:=clock_timestamp();
  INSERT INTO expert_review_assignments(review_kind,review_id,assignee_id,assigned_at,source,assigned_by,reason) VALUES(kind,p_resource,target.id,stamp,'Reassigned',p_actor,reason_text)
   ON CONFLICT (review_kind,review_id) DO UPDATE SET assignee_id=EXCLUDED.assignee_id,assigned_at=EXCLUDED.assigned_at,source='Reassigned',
    assigned_by=EXCLUDED.assigned_by,reason=EXCLUDED.reason;
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update'::audit_action_enum,'ExpertReviewAssignment',p_resource,gen_random_uuid(),
   jsonb_build_object('kind',kind,'assigneeId',current_assignee),
   jsonb_build_object('auditContractVersion',1,'kind',kind,'assigneeId',target.id,'assignedBy',p_actor,'reason',reason_text,'source','Reassigned'),now());
 END IF;
 RETURN jsonb_build_object('code','OK','result',CASE WHEN kind='Scenario' THEN expert_review_item(p_resource) ELSE expert_infrastructure_item(p_resource) END);
END $$;

DO $grants$ DECLARE s record;sig text;r text;BEGIN
 FOREACH sig IN ARRAY ARRAY['expert_is_available(uuid)','expert_next_assignee()','expert_assign_pending()','expert_review_assign_on_submit()',
  'expert_review_assignment(text,uuid)','expert_review_item(uuid)','expert_infrastructure_item(uuid)',
  'expert_review_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','expert_review_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION expert_review_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);
  EXECUTE format('GRANT EXECUTE ON FUNCTION expert_review_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);END IF;END LOOP;
 -- Items already waiting when this migration runs get their assignee now.
 PERFORM expert_assign_pending();
 SELECT * INTO s FROM expert_assignment_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
