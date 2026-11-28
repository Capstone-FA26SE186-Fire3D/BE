-- BE#245: fixes around thẩm định assignment (BE#242) and the first sign-in (BE#235).
-- 1. An expert created before BE#235 has no email_verified_at and first_login_step NULL; BE#242 never gave them work. Available is
--    now: active, not deleted and not at the VerifyEmail step (an account created since BE#235 leaves that step only by verifying).
-- 2. PlatformAdmin no longer approves or rejects scenario content through scenario_readiness_gate (410 ADMIN_REVIEW_REMOVED); thẩm
--    định is the assigned FireSafetyExpert's (FR-PLAN-05, BE#242). Body = the live one with that line added.
-- 3. One lock order: Building, then fire3d:expert-assignment. A decision and a reassignment hand out waiting items only after they
--    hold the Building lock, like a submission (its trigger runs inside the submitting gate), so the two cannot deadlock (40P01).
-- 4. An expert who becomes unavailable (locked, deleted, back at VerifyEmail) gives back the items still waiting for them; they are
--    handed out again at once. expert_assign_pending counts only the rows it inserted.
CREATE TEMP TABLE expert_fix_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO expert_fix_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION expert_is_available(p_user uuid) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT EXISTS(SELECT 1 FROM users u WHERE u.id=p_user AND u.role::text='FireSafetyExpert' AND u.is_active AND u.deleted_at IS NULL
  AND u.first_login_step IS DISTINCT FROM 'VerifyEmail')
$$;

CREATE OR REPLACE FUNCTION expert_assign_pending() RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE item record;who uuid;n integer:=0;added integer;
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
  GET DIAGNOSTICS added = ROW_COUNT;
  n:=n+added;
 END LOOP;
 RETURN n;
END $$;

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
 -- BE#242: items that waited while no expert was available are handed out. Reads do it here; a decision does it only after it
 -- holds the Building lock (below), the order a submission takes (Building, then fire3d:expert-assignment), so the two never
 -- deadlock (BE#245).
 IF p_action IN('List','Get','Preview') THEN PERFORM expert_assign_pending(); END IF;

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
  PERFORM expert_assign_pending();
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
 PERFORM expert_assign_pending();
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
 IF p_action='List' THEN
  -- Only the read hands out waiting items first; Reassign takes the Building lock before fire3d:expert-assignment (BE#245).
  PERFORM expert_assign_pending();
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

CREATE OR REPLACE FUNCTION public.scenario_readiness_gate(p_action text, p_actor uuid, p_family uuid, p_version uuid, p_revision uuid, p_input jsonb, p_key text)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog', 'public', 'pg_temp'
AS $function$
DECLARE actor users;v scenario_versions;b buildings;run validation_runs;art revision_artifacts;review scenario_content_reviews;receipt readiness_command_receipts;
 h text:=fet3d_jsonb_payload_hash(jsonb_build_object('version',p_version,'revision',p_revision,'input',p_input));k text:=p_key;result jsonb;new_id uuid;existing revision_reviews;before_values jsonb := '{}';after_values jsonb;
BEGIN
 IF p_action NOT IN('Confirm','TechnicalReject','Submit','Approve','Reject') THEN RETURN jsonb_build_object('code','READINESS_ACTION_INVALID','status',400);END IF;
 IF p_action IN('Confirm','TechnicalReject') AND k IS NULL THEN k:='technical:'||h;END IF;
 IF k IS NULL OR length(k) NOT BETWEEN 1 AND 128 OR k ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 IF p_family IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family
  AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 -- BE#245: thẩm định belongs to FireSafetyExpert through expert_review_gate (assignee only, BE#242). PlatformAdmin no longer
 -- approves or rejects here; nobody else ever could.
 IF p_action IN('Approve','Reject') AND actor.role='PlatformAdmin' THEN RETURN jsonb_build_object('code','ADMIN_REVIEW_REMOVED','status',410);END IF;
 IF (p_action IN('Approve','Reject') AND actor.role<>'PlatformAdmin') OR (p_action='Submit' AND actor.role<>'OrganizationUser') OR actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO v FROM scenario_versions WHERE id=p_version;
 SELECT * INTO b FROM buildings WHERE id=v.building_id;
 IF v.id IS NULL OR b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) OR (p_revision IS NOT NULL AND p_revision<>v.revision_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 SELECT * INTO v FROM scenario_versions WHERE id=p_version FOR SHARE;
 PERFORM 1 FROM revisions WHERE id=v.revision_id FOR UPDATE;
 PERFORM pg_advisory_xact_lock(hashtextextended('readiness:'||p_actor||':'||p_action||':'||k,0));
 IF p_family IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family
  AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 SELECT * INTO receipt FROM readiness_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=k;
 IF receipt.result IS NOT NULL THEN IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;RETURN receipt.result;END IF;
 IF p_action IN('Confirm','TechnicalReject') THEN
  SELECT vr.* INTO run FROM validation_runs vr JOIN processing_jobs j ON j.id=vr.job_id JOIN processing_job_attempts a ON a.id=vr.processing_attempt_id AND j.current_attempt_id=a.id
   WHERE vr.id=(p_input->>'validationRunId')::uuid AND vr.revision_id=v.revision_id AND vr.scenario_version_id=v.id AND vr.scenario_hash=v.scenario_hash AND j.status='Succeeded' AND a.status='Succeeded';
  SELECT * INTO art FROM revision_artifacts WHERE id=run.candidate_artifact_id AND revision_id=v.revision_id AND attempt_id=run.processing_attempt_id AND job_id=run.job_id;
  IF run.id IS NULL OR art.id IS NULL OR run.annotation_set_id IS DISTINCT FROM (p_input->>'annotationSetId')::uuid OR (run.annotation_set_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM annotation_sets WHERE id=run.annotation_set_id AND revision_id=v.revision_id)) THEN RETURN jsonb_build_object('code','READINESS_PROVENANCE_MISMATCH','status',409);END IF;
  IF p_action='Confirm' AND (run.outcome<>'Passed' OR NOT art.is_runtime_ready OR EXISTS(SELECT 1 FROM revision_issues WHERE validation_run_id=run.id AND severity IN('Error','Critical'))) THEN RETURN jsonb_build_object('code','READINESS_BLOCKED','status',409);END IF;
  IF p_action='TechnicalReject' AND (NULLIF(btrim(p_input->>'reviewMessage'),'') IS NULL OR length(p_input->>'reviewMessage')>4000) THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reviewMessage',jsonb_build_array('A reason of 1-4000 characters is required.')));END IF;
  IF p_action='Confirm' THEN
   SELECT * INTO existing FROM revision_reviews WHERE scenario_version_id=v.id AND action='ConfirmForTraining';
   IF existing.id IS NOT NULL THEN
    IF existing.validation_run_id IS DISTINCT FROM run.id OR existing.annotation_set_id IS DISTINCT FROM run.annotation_set_id THEN RETURN jsonb_build_object('code','CONFIRMATION_ALREADY_EXISTS','status',409);END IF;
    result:=jsonb_build_object('code','OK','reviewId',existing.id);
   END IF;
  END IF;
  IF result IS NULL THEN
   new_id:=gen_random_uuid();INSERT INTO revision_reviews(id,revision_id,scenario_version_id,reviewed_by,validation_run_id,annotation_set_id,review_message,reviewed_at,action)
    VALUES(new_id,v.revision_id,v.id,p_actor,run.id,run.annotation_set_id,p_input->>'reviewMessage',now(),CASE WHEN p_action='Confirm' THEN 'ConfirmForTraining'::review_action_enum ELSE 'Rejected'::review_action_enum END);
   IF p_action='Confirm' THEN UPDATE revisions SET status='ConfirmedForTraining',updated_at=now() WHERE id=v.revision_id;END IF;
   result:=jsonb_build_object('code','OK','reviewId',new_id);
  END IF;
 ELSE
  IF v.state_snapshot IS NULL OR v.rubric IS NULL OR v.scenario_hash IS DISTINCT FROM fet3d_jsonb_payload_hash(v.state_snapshot) THEN RETURN jsonb_build_object('code','SCENARIO_SNAPSHOT_REQUIRED','status',409);END IF;
  SELECT * INTO review FROM scenario_content_reviews WHERE scenario_version_id=v.id FOR UPDATE;
  IF review.id IS NOT NULL THEN before_values := jsonb_build_object('status',review.status,'reviewId',review.id,'scenarioVersionId',v.id,'submittedBy',review.submitted_by,'submittedAt',review.submitted_at,'reviewedBy',review.reviewed_by,'reviewedAt',review.reviewed_at); END IF;
  IF NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family
   AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
  IF p_action='Submit' THEN
   IF review.id IS NOT NULL THEN RETURN jsonb_build_object('code','CONTENT_REVIEW_ALREADY_EXISTS','status',409);END IF;
   new_id:=gen_random_uuid();INSERT INTO scenario_content_reviews(id,scenario_version_id,status,content_hash,rubric_hash,submitted_by) VALUES(new_id,v.id,'Submitted',v.scenario_hash,fet3d_jsonb_payload_hash(v.rubric),p_actor);
   SELECT * INTO review FROM scenario_content_reviews WHERE id=new_id;
  ELSE
   IF review.id IS NULL OR review.status<>'Submitted' THEN RETURN jsonb_build_object('code','CONTENT_REVIEW_NOT_PENDING','status',409);END IF;
   IF review.content_hash IS DISTINCT FROM p_input->>'contentHash' OR review.rubric_hash IS DISTINCT FROM p_input->>'rubricHash' OR review.content_hash<>v.scenario_hash OR review.rubric_hash<>fet3d_jsonb_payload_hash(v.rubric) THEN RETURN jsonb_build_object('code','CONTENT_HASH_MISMATCH','status',409);END IF;
   IF p_action='Reject' AND (NULLIF(btrim(p_input->>'reason'),'') IS NULL OR length(p_input->>'reason')>4000) THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('A rejection reason of 1-4000 characters is required.')));END IF;
   UPDATE scenario_content_reviews SET status=CASE WHEN p_action='Approve' THEN 'Approved' ELSE 'Rejected' END,reviewed_by=p_actor,reviewed_at=now(),reason=p_input->>'reason' WHERE id=review.id RETURNING * INTO review;
  END IF;
  result:=jsonb_build_object('code','OK','reviewId',review.id,'status',review.status,'contentHash',review.content_hash,'rubricHash',review.rubric_hash);
 END IF;
 INSERT INTO readiness_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,k,h,result);
 IF p_action IN('Confirm','TechnicalReject') THEN
  SELECT jsonb_build_object('auditContractVersion',1,'status',rr.action::text,'reviewId',rr.id,
   'scenarioVersionId',rr.scenario_version_id,'revisionId',rr.revision_id,'validationRunId',rr.validation_run_id,
   'candidateArtifactId',art.id,'annotationSetId',rr.annotation_set_id,'reviewedBy',rr.reviewed_by,'reviewedAt',rr.reviewed_at)
   INTO after_values FROM revision_reviews rr WHERE rr.id=(result->>'reviewId')::uuid;
 ELSE
  after_values := jsonb_build_object('auditContractVersion',1,'status',review.status,'reviewId',review.id,
   'scenarioVersionId',v.id,'submittedBy',review.submitted_by,'submittedAt',review.submitted_at,
   'reviewedBy',review.reviewed_by,'reviewedAt',review.reviewed_at);
 END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,old_values,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User',CASE WHEN p_action='Confirm' THEN 'ConfirmForTraining'::audit_action_enum
   WHEN p_action IN('Reject','TechnicalReject') THEN 'Reject'::audit_action_enum ELSE 'Update'::audit_action_enum END,
   'ScenarioVersion',v.id,gen_random_uuid(),before_values,after_values,now());
 RETURN result;
END $function$;

-- An expert who can no longer take work gives back what still waits for them; the rest of the team gets it in turn.
CREATE OR REPLACE FUNCTION expert_release_when_unavailable() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
BEGIN
 IF OLD.is_active AND OLD.deleted_at IS NULL AND OLD.first_login_step IS DISTINCT FROM 'VerifyEmail' AND NOT expert_is_available(NEW.id) THEN
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:expert-assignment',0));
  DELETE FROM expert_review_assignments a WHERE a.assignee_id=NEW.id AND (
   (a.review_kind='Scenario' AND EXISTS(SELECT 1 FROM scenario_content_reviews r WHERE r.id=a.review_id AND r.status='Submitted'))
   OR (a.review_kind='Infrastructure' AND EXISTS(SELECT 1 FROM fire_infrastructures f WHERE f.id=a.review_id AND f.status='Submitted')));
  PERFORM expert_assign_pending();
 END IF;
 RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS users_expert_release_when_unavailable ON public.users;
CREATE TRIGGER users_expert_release_when_unavailable AFTER UPDATE OF is_active,deleted_at,first_login_step ON public.users FOR EACH ROW
 WHEN (OLD.role::text='FireSafetyExpert') EXECUTE FUNCTION expert_release_when_unavailable();
GRANT DELETE ON public.expert_review_assignments TO fet3d_ifc_upload_owner;

DO $grants$ DECLARE s record;sig text;r text;BEGIN
 FOREACH sig IN ARRAY ARRAY['expert_is_available(uuid)','expert_assign_pending()','expert_release_when_unavailable()',
  'expert_review_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','expert_review_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint)',
  'scenario_readiness_gate(text,uuid,uuid,uuid,uuid,jsonb,text)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION expert_review_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);
  EXECUTE format('GRANT EXECUTE ON FUNCTION expert_review_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);
  EXECUTE format('GRANT EXECUTE ON FUNCTION scenario_readiness_gate(text,uuid,uuid,uuid,uuid,jsonb,text) TO %I',r);END IF;END LOOP;
 -- Experts created before BE#235 can take the items already waiting.
 PERFORM expert_assign_pending();
 SELECT * INTO s FROM expert_fix_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
