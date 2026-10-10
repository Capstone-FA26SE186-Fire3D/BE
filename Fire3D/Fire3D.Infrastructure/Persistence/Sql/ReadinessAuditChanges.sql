CREATE TEMP TABLE readiness_audit_owner_state(os boolean,oi boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;hc boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 hc:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 INSERT INTO readiness_audit_owner_state VALUES(os,oi,c,hc);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user); END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;
GRANT SELECT ON auth_refresh_tokens TO fet3d_ifc_upload_owner;

CREATE OR REPLACE FUNCTION scenario_readiness_gate(p_action text,p_actor uuid,p_family uuid,p_version uuid,p_revision uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
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
END $$;
-- Every legacy caller fails closed, including a login with an old explicit grant.
CREATE OR REPLACE FUNCTION scenario_readiness_gate(p_action text,p_actor uuid,p_version uuid,p_revision uuid,p_input jsonb,p_key text)
RETURNS jsonb LANGUAGE sql SECURITY INVOKER SET search_path=pg_catalog,public AS $$ SELECT jsonb_build_object('code','UNAUTHORIZED','status',401) $$;
ALTER FUNCTION scenario_readiness_gate(text,uuid,uuid,uuid,uuid,jsonb,text) OWNER TO fet3d_ifc_upload_owner;
REVOKE ALL ON FUNCTION scenario_readiness_gate(text,uuid,uuid,uuid,uuid,jsonb,text),scenario_readiness_gate(text,uuid,uuid,uuid,jsonb,text) FROM PUBLIC;
DO $$ DECLARE r text;s readiness_audit_owner_state;BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated','fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   EXECUTE format('REVOKE ALL ON FUNCTION scenario_readiness_gate(text,uuid,uuid,uuid,jsonb,text) FROM %I',r);
   IF r IN('fire3d_api','fet3d_backend_executor') THEN
    EXECUTE format('GRANT EXECUTE ON FUNCTION scenario_readiness_gate(text,uuid,uuid,uuid,uuid,jsonb,text) TO %I',r);
   ELSE EXECUTE format('REVOKE ALL ON FUNCTION scenario_readiness_gate(text,uuid,uuid,uuid,uuid,jsonb,text) FROM %I',r); END IF;
  END IF;
 END LOOP;
 SELECT * INTO s FROM readiness_audit_owner_state;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner; END IF;
 IF s.changed THEN
  IF s.os IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.oi THEN 'TRUE' ELSE 'FALSE' END); END IF;
 END IF;
END $$;
