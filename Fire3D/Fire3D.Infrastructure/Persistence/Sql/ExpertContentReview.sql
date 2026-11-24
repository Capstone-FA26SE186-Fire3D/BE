-- BE#211 (FR-PLAN-05): FireSafetyExpert accounts thẩm định scenario content instead of PlatformAdmin.
-- * users: an expert has no organization, like PlatformAdmin and Trainee. Accounts are created by PlatformAdmin only.
-- * expert_review_gate: one shared queue (no assignment) of scenario content reviews from every organization; List/Get/Preview read
--   a review in any status (decided ones are read-only), Approve/Reject decide a Submitted one. A decision needs Idempotency-Key and
--   If-Match: a review row changes once (Submitted -> decided), so its revision is 1 while Submitted and 2 once decided and the
--   expert who comes second gets 412. Decisions are final (a fix is a new version and a new submission); reject needs a reason of
--   1-2000 characters. The expert reads only reviews: never drafts, billing, or other organization data.
-- * The submitter gets a web notification (ContentReviewApproved / ContentReviewRejected) in organization_notifications.
-- The PlatformAdmin routes of scenario_readiness_gate are left as they are for now (deprecated; removal is tracked in the docs).
-- Wording: "thẩm định", never "phê duyệt phương án" (Điều 15 Nghị định 105/2025/NĐ-CP).
CREATE TEMP TABLE expert_review_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO expert_review_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- An expert has no organization. role::text so the new label is never cast while this transaction runs.
ALTER TABLE public.users DROP CONSTRAINT IF EXISTS check_user_role_organization;
DO $$ DECLARE violations bigint;BEGIN
 SELECT count(*) INTO violations FROM public.users WHERE NOT (
  (role::text='OrganizationUser' AND organization_id IS NOT NULL) OR (role::text IN('PlatformAdmin','Trainee','FireSafetyExpert') AND organization_id IS NULL));
 IF violations=0 THEN
  ALTER TABLE public.users ADD CONSTRAINT check_user_role_organization CHECK (
   (role::text='OrganizationUser' AND organization_id IS NOT NULL) OR (role::text IN('PlatformAdmin','Trainee','FireSafetyExpert') AND organization_id IS NULL));
 ELSE RAISE NOTICE 'check_user_role_organization skipped: % users violate it',violations;
 END IF;
END $$;

-- Review notifications: no entitlement or expiry; they point at the content review instead.
ALTER TABLE organization_notifications ALTER COLUMN entitlement_id DROP NOT NULL,ALTER COLUMN reference_ends_at DROP NOT NULL,
 ADD COLUMN content_review_id uuid REFERENCES scenario_content_reviews(id) ON DELETE RESTRICT,
 ADD COLUMN scenario_version_id uuid REFERENCES scenario_versions(id) ON DELETE RESTRICT;
ALTER TABLE organization_notifications DROP CONSTRAINT check_org_notification_type;
ALTER TABLE organization_notifications ADD CONSTRAINT check_org_notification_type CHECK (notification_type IN('BuildingServiceExpiring','ContentReviewApproved','ContentReviewRejected'));
ALTER TABLE organization_notifications ADD CONSTRAINT check_org_notification_reference CHECK (
 (notification_type='BuildingServiceExpiring' AND entitlement_id IS NOT NULL AND reference_ends_at IS NOT NULL AND content_review_id IS NULL AND scenario_version_id IS NULL)
 OR (notification_type IN('ContentReviewApproved','ContentReviewRejected') AND content_review_id IS NOT NULL AND scenario_version_id IS NOT NULL AND entitlement_id IS NULL AND reference_ends_at IS NULL));

CREATE OR REPLACE FUNCTION validate_organization_notification_write()
RETURNS TRIGGER LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (
        OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.recipient_user_id IS DISTINCT FROM NEW.recipient_user_id
        OR OLD.building_id IS DISTINCT FROM NEW.building_id
        OR OLD.entitlement_id IS DISTINCT FROM NEW.entitlement_id
        OR OLD.content_review_id IS DISTINCT FROM NEW.content_review_id
        OR OLD.scenario_version_id IS DISTINCT FROM NEW.scenario_version_id
        OR OLD.notification_type IS DISTINCT FROM NEW.notification_type
        OR OLD.reference_ends_at IS DISTINCT FROM NEW.reference_ends_at
    ) THEN
        RAISE EXCEPTION 'notification identity and tenant scope are immutable';
    END IF;
    IF NOT EXISTS (
            SELECT 1 FROM public.users AS recipient
             WHERE recipient.id = NEW.recipient_user_id AND recipient.organization_id = NEW.organization_id AND recipient.role = 'OrganizationUser')
       OR NOT EXISTS (SELECT 1 FROM public.buildings AS building WHERE building.id = NEW.building_id AND building.organization_id = NEW.organization_id) THEN
        RAISE EXCEPTION 'notification recipient and Building must belong to the organization';
    END IF;
    IF NEW.notification_type = 'BuildingServiceExpiring' THEN
        IF NOT EXISTS (
            SELECT 1 FROM public.service_entitlements AS entitlement
             WHERE entitlement.id = NEW.entitlement_id AND entitlement.organization_id = NEW.organization_id
               AND entitlement.building_id = NEW.building_id AND entitlement.ends_at = NEW.reference_ends_at) THEN
            RAISE EXCEPTION 'expiry notification recipient, Building and entitlement must share the organization and expiry';
        END IF;
    ELSIF NEW.notification_type IN ('ContentReviewApproved','ContentReviewRejected') THEN
        IF NOT EXISTS (
            SELECT 1 FROM public.scenario_content_reviews AS review JOIN public.scenario_versions AS version ON version.id = review.scenario_version_id
             WHERE review.id = NEW.content_review_id AND version.id = NEW.scenario_version_id AND version.building_id = NEW.building_id
               AND review.status = CASE NEW.notification_type WHEN 'ContentReviewApproved' THEN 'Approved' ELSE 'Rejected' END) THEN
            RAISE EXCEPTION 'review notification must match a decided review of the Building';
        END IF;
    ELSE
        RAISE EXCEPTION 'unknown organization notification type';
    END IF;
    RETURN NEW;
END;
$$;
GRANT SELECT,INSERT ON organization_notifications TO fet3d_ifc_upload_owner;

-- Expert view of one review: the shared summary plus the queue kind, the ETag revision and the revision the scenario uses.
CREATE FUNCTION expert_review_item(p_review uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog,public,pg_temp AS $$
 SELECT scenario_review_summary(r.id)||jsonb_build_object('kind','Scenario','revision',CASE WHEN r.status='Submitted' THEN 1 ELSE 2 END,'revisionId',v.revision_id)
 FROM scenario_content_reviews r JOIN scenario_versions v ON v.id=r.scenario_version_id WHERE r.id=p_review
$$;

CREATE FUNCTION expert_review_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public,pg_temp AS $$
DECLARE actor users;r scenario_content_reviews;v scenario_versions;b buildings;s scenarios;receipt readiness_command_receipts;
 requested_kind text;requested_status text;page_number integer;page_size integer;total integer;result jsonb;h text;before_values jsonb;decision_reason text;stamp timestamptz;
BEGIN
 IF p_action NOT IN('List','Get','Preview','Approve','Reject') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR p_family IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 -- Only experts; PlatformAdmin has no thẩm định path, not even while no expert exists (FR-PLAN-05).
 IF actor.role::text<>'FireSafetyExpert' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;

 IF p_action='List' THEN
  requested_kind:=p_input->>'kind';requested_status:=COALESCE(p_input->>'status','Submitted');
  page_number:=COALESCE((p_input->>'page')::int,1);page_size:=COALESCE((p_input->>'pageSize')::int,20);
  IF (requested_kind IS NOT NULL AND requested_kind NOT IN('Scenario','Infrastructure')) OR requested_status NOT IN('Submitted','Approved','Rejected')
   OR page_number<1 OR page_size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  -- Infrastructure reviews arrive with #213; until then that kind has no rows.
  IF requested_kind='Infrastructure' THEN
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items','[]'::jsonb,'totalCount',0,'page',page_number,'pageSize',page_size));END IF;
  SELECT count(*) INTO total FROM scenario_content_reviews WHERE status=requested_status;
  -- Waiting items oldest first (first come, first reviewed); decided items newest decision first.
  SELECT COALESCE(jsonb_agg(expert_review_item(q.id) ORDER BY q.rn),'[]'::jsonb) INTO result FROM (
   SELECT id,row_number() OVER(ORDER BY CASE WHEN requested_status='Submitted' THEN submitted_at END,
    CASE WHEN requested_status<>'Submitted' THEN reviewed_at END DESC,id) rn
   FROM scenario_content_reviews WHERE status=requested_status
   ORDER BY CASE WHEN requested_status='Submitted' THEN submitted_at END,CASE WHEN requested_status<>'Submitted' THEN reviewed_at END DESC,id
   LIMIT page_size OFFSET (page_number::bigint-1)*page_size) q;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'totalCount',total,'page',page_number,'pageSize',page_size));
 END IF;

 SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource;
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
 decision_reason:=NULLIF(btrim(p_input->>'reason'),'');
 IF p_action='Reject' AND (decision_reason IS NULL OR length(decision_reason)>2000) THEN
  RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('reason',jsonb_build_array('A rejection reason of 1-2000 characters is required.')));END IF;
 IF p_action='Approve' THEN decision_reason:=NULL;END IF;
 -- Same lock order as scenario_readiness_gate: Building, then the review row.
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 SELECT * INTO r FROM scenario_content_reviews WHERE id=p_resource FOR UPDATE;
 IF p_expected<>(CASE WHEN r.status='Submitted' THEN 1 ELSE 2 END) THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
 IF r.status<>'Submitted' THEN RETURN jsonb_build_object('code','CONTENT_REVIEW_NOT_PENDING','status',409);END IF;
 IF r.content_hash<>v.scenario_hash OR r.rubric_hash<>fet3d_jsonb_payload_hash(v.rubric) THEN RETURN jsonb_build_object('code','CONTENT_HASH_MISMATCH','status',409);END IF;
 before_values:=jsonb_build_object('status',r.status,'reviewId',r.id,'scenarioVersionId',v.id,'submittedBy',r.submitted_by,'submittedAt',r.submitted_at,'reviewedBy',r.reviewed_by,'reviewedAt',r.reviewed_at);
 stamp:=clock_timestamp();
 UPDATE scenario_content_reviews SET status=CASE WHEN p_action='Approve' THEN 'Approved' ELSE 'Rejected' END,reviewed_by=p_actor,reviewed_at=stamp,reason=decision_reason
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

DO $grants$ DECLARE s record;sig text;r text;BEGIN
 FOREACH sig IN ARRAY ARRAY['expert_review_item(uuid)','expert_review_gate(text,uuid,uuid,uuid,jsonb,text,bigint)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION expert_review_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM expert_review_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
