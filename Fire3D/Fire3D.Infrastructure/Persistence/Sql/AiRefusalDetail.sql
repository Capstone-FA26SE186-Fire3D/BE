-- BE#188: keep what the AI service said when it refused the pinned input. ai_worker_gate keeps the body of AiContractV2 with only the Reject
-- action changed: besides the failure code, provider_result now holds {"refusal":{httpStatus,code,message,documentId}} (Admin views only; the
-- Organization and Trainee views never read provider_result). AI_SOURCE_HASH_MISMATCH and AI_SOURCE_UNAVAILABLE can then be traced to the source
-- named by the service. Requests that already failed keep a NULL provider_result.
CREATE TEMP TABLE ai_refusal_permissions(original_set boolean,original_inherit boolean,changed boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ai_refusal_permissions VALUES(os,oi,c);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
END $$;

CREATE OR REPLACE FUNCTION ai_worker_gate(p_action text,p_worker text,p_request uuid,p_lease uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE r ai_requests;a record;phase text;units integer;remaining integer;take integer;st text;bad text;rtype text;now_bim jsonb;
 lease_seconds integer:=COALESCE((p_input->>'leaseSeconds')::int,180);stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('Claim','Complete','Retry','Unknown','Reject','NotFound') OR NULLIF(btrim(p_worker),'') IS NULL THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 IF p_action='Claim' THEN
  IF lease_seconds NOT BETWEEN 10 AND 3600 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  -- A crashed dispatch may have reached the provider: never re-send, hold the reservation for reconciliation.
  UPDATE ai_requests SET status='NeedsReconcile',failure_code='AI_OUTCOME_UNKNOWN',lease_token=NULL,lease_until=NULL,updated_at=stamp,revision=revision+1 WHERE status='Processing' AND lease_until<=stamp;
  UPDATE ai_requests SET lease_token=NULL,lease_until=NULL,reconcile_requested_at=COALESCE(reconcile_requested_at,stamp),updated_at=stamp WHERE status='NeedsReconcile' AND lease_until<=stamp;
  SELECT * INTO r FROM ai_requests WHERE (status='Accepted' AND next_attempt_at<=stamp) OR (status='NeedsReconcile' AND reconcile_requested_at IS NOT NULL AND lease_token IS NULL)
   ORDER BY created_at,id LIMIT 1 FOR UPDATE SKIP LOCKED;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','EMPTY');END IF;
  phase:=CASE WHEN r.status='Accepted' THEN 'Dispatch' ELSE 'Lookup' END;
  UPDATE ai_requests SET status=CASE WHEN phase='Dispatch' THEN 'Processing' ELSE status END,attempts=attempts+CASE WHEN phase='Dispatch' THEN 1 ELSE 0 END,
   lease_token=gen_random_uuid(),lease_until=stamp+make_interval(secs=>lease_seconds),reconcile_requested_at=NULL,updated_at=stamp WHERE id=r.id RETURNING * INTO r;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'phase',phase,'leaseToken',r.lease_token,'operation',r.operation,'audience',r.audience,
   'payload',jsonb_build_object('request_id',r.id,'audience',r.audience,'operation',r.operation,
    'response_type',CASE WHEN r.operation='ScenarioDraft' THEN 'ScenarioDraft' ELSE 'KnowledgeAnswer' END,
    'organization_id',r.organization_id,'input',jsonb_build_object('text',COALESCE(r.input->>'question',r.input->>'prompt')),'input_hash',r.input_hash,
    'sources',COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',s->>'kind','document_id',s->'id','document_version',s->'versionId','title',s->'title',
      'version_label',s->'versionLabel','content_hash',s->'contentHash','source_uri',s->'link'->'sourceUri',
      'content',COALESCE(ai_source_content(s->>'kind',(s->>'versionId')::uuid),s->'content'),
      'content_omitted',s->>'kind' IN('LearnPost','LibraryVersion') AND ai_source_content(s->>'kind',(s->>'versionId')::uuid) IS NULL) ORDER BY o)
     FROM jsonb_array_elements(r.sources) WITH ORDINALITY e(s,o)),'[]'),
    'bim',r.bim,
    'learn_context',CASE WHEN r.context->>'kind'='Learn' THEN jsonb_build_object('post_id',r.context->'postId','version_id',r.context->'versionId') END,
    'context',r.context,'limits',jsonb_build_object('quota_unit',r.quota_unit,'max_units',r.reserved_units),'attempt',r.attempts)));
 END IF;

 SELECT * INTO r FROM ai_requests WHERE id=p_request FOR UPDATE;
 IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_lease IS NULL OR r.lease_token IS NULL OR r.lease_until IS NULL OR r.lease_token<>p_lease OR r.lease_until<=stamp OR r.status NOT IN('Processing','NeedsReconcile') THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
 phase:=CASE WHEN r.status='Processing' THEN 'Dispatch' ELSE 'Lookup' END;

 IF p_action='Complete' THEN
  st:=p_input->>'status';
  units:=CASE WHEN jsonb_typeof(p_input->'usage'->'units')='number' AND (p_input->'usage'->>'units') ~ '^[0-9]{1,9}$' THEN (p_input->'usage'->>'units')::int END;
  rtype:=CASE st WHEN 'Answered' THEN 'KnowledgeAnswer' WHEN 'NeedsUserEdit' THEN 'ScenarioDraft' WHEN 'InsufficientEvidence' THEN 'InsufficientEvidence'
   WHEN 'RejectedBySafetyGate' THEN 'RejectedBySafetyGate' END;
  IF r.operation='ScenarioDraft' AND st='NeedsUserEdit' THEN now_bim:=ai_bim_snapshot((r.bim->>'revisionId')::uuid);END IF;
  bad:=CASE
   WHEN p_input->>'requestId' IS DISTINCT FROM r.id::text OR rtype IS NULL OR jsonb_typeof(p_input->'result') IS DISTINCT FROM 'object'
     OR jsonb_typeof(p_input->'citations') IS DISTINCT FROM 'array' OR jsonb_typeof(COALESCE(p_input->'bimAnchors','[]')) IS DISTINCT FROM 'array' THEN 'AI_RESULT_INVALID'
   -- NeedsUserEdit belongs to ScenarioDraft only, Answered to knowledge answers only.
   WHEN (st='NeedsUserEdit')<>(r.operation='ScenarioDraft' AND st='NeedsUserEdit') OR (st='Answered' AND r.operation='ScenarioDraft') THEN 'AI_RESULT_INVALID'
   WHEN p_input->>'responseType' IS DISTINCT FROM CASE WHEN r.operation='ScenarioDraft' THEN 'ScenarioDraft' ELSE 'KnowledgeAnswer' END THEN 'AI_RESULT_INVALID'
   WHEN units IS NULL THEN 'AI_USAGE_INVALID'
   WHEN units>r.reserved_units THEN 'AI_USAGE_EXCEEDS_RESERVATION'
   -- Citations name a pinned document version that is still eligible; the provider's own tenant filter is not trusted.
   WHEN EXISTS(SELECT 1 FROM jsonb_array_elements(p_input->'citations') c WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(r.sources) s
     WHERE s->>'id'=c->>'documentId' AND s->>'versionId'=c->>'documentVersion'
     AND (s->>'kind' IN('TrainingSnapshot','SessionResult')
      OR (r.audience='organization' AND EXISTS(SELECT 1 FROM ai_eligible_sources(r.organization_id) e WHERE e.kind=s->>'kind' AND e.id::text=s->>'id' AND e.version_id::text=s->>'versionId'))
      OR (r.audience='trainee' AND EXISTS(SELECT 1 FROM ai_trainee_eligible_sources() e WHERE e.kind=s->>'kind' AND e.id::text=s->>'id' AND e.version_id::text=s->>'versionId'))))) THEN 'AI_CITATION_INVALID'
   WHEN st IN('Answered','NeedsUserEdit') AND jsonb_array_length(p_input->'citations')=0 THEN 'AI_CITATION_INVALID'
   WHEN r.operation<>'ScenarioDraft' AND jsonb_array_length(COALESCE(p_input->'bimAnchors','[]'))>0 THEN 'AI_RESULT_INVALID'
   -- A draft must anchor to the pinned revision, keep its geometry pin, and still pass the editor reference check.
   WHEN st='NeedsUserEdit' AND (now_bim IS NULL OR now_bim->'geometry'->>'artifactId' IS DISTINCT FROM r.bim->'geometry'->>'artifactId') THEN 'AI_REVISION_STALE'
   WHEN st='NeedsUserEdit' AND (jsonb_array_length(COALESCE(p_input->'bimAnchors','[]'))=0
     OR EXISTS(SELECT 1 FROM jsonb_array_elements(p_input->'bimAnchors') b WHERE b->>'revisionId' IS DISTINCT FROM r.bim->>'revisionId'
      OR NOT (EXISTS(SELECT 1 FROM jsonb_array_elements(r.bim->'anchors') x WHERE x->>'ifcGlobalId'=b->>'ifcGlobalId')
       OR EXISTS(SELECT 1 FROM jsonb_array_elements(r.bim->'floors') f WHERE f->>'ifcGlobalId'=b->>'ifcGlobalId')))
     OR p_input->'result'->'draft'->'state'->'geometry'->>'artifactId' IS DISTINCT FROM r.bim->'geometry'->>'artifactId'
     OR p_input->'result'->'draft'->'state'->'geometry'->>'sha256Hash' IS DISTINCT FROM r.bim->'geometry'->>'sha256Hash'
     OR jsonb_array_length(scenario_reference_issues((r.bim->>'revisionId')::uuid,p_input->'result'->'draft'->'state'))>0) THEN 'AI_BIM_ANCHOR_INVALID'
  END;
  IF bad IS NOT NULL THEN
   UPDATE ai_requests SET status='NeedsReconcile',failure_code=bad,provider_result=p_input,lease_token=NULL,lease_until=NULL,updated_at=stamp,revision=revision+1 WHERE id=r.id;
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'status','NeedsReconcile','failureCode',bad));
  END IF;
  remaining:=units;
  FOR a IN SELECT al.id,al.reserved_units FROM billing_ai_quota_allocations al JOIN billing_ai_quota_grants gr ON gr.id=al.grant_id
   WHERE al.request_id=r.id AND al.status='Reserved' ORDER BY gr.ends_at,gr.created_at,gr.id FOR UPDATE OF al LOOP
   take:=least(remaining,a.reserved_units);
   IF take>0 THEN UPDATE billing_ai_quota_allocations SET status='Settled',consumed_units=take,settled_at=stamp WHERE id=a.id;
   ELSE UPDATE billing_ai_quota_allocations SET status='Released',settled_at=stamp WHERE id=a.id;END IF;
   remaining:=remaining-take;
  END LOOP;
  UPDATE ai_requests SET status='Succeeded',result_type=rtype,consumed_units=units,
   result=jsonb_build_object('status',st)||(p_input->'result')||jsonb_build_object('citations',p_input->'citations','bimAnchors',COALESCE(p_input->'bimAnchors','[]')),
   model_version=left(p_input->>'modelVersion',120),technical_usage=jsonb_strip_nulls(jsonb_build_object('inputTokens',p_input->'usage'->'inputTokens','outputTokens',p_input->'usage'->'outputTokens')),
   provider_result=NULL,failure_code=NULL,lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSIF p_action='Retry' THEN
  IF phase<>'Dispatch' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  IF r.attempts<LEAST(GREATEST(COALESCE((p_input->>'maxAttempts')::int,3),1),10) THEN
   UPDATE ai_requests SET status='Accepted',lease_token=NULL,lease_until=NULL,next_attempt_at=stamp+make_interval(secs=>LEAST(GREATEST(COALESCE((p_input->>'backoffSeconds')::int,30),1),3600)*r.attempts),
    failure_code=left(p_input->>'reason',80),updated_at=stamp,revision=revision+1 WHERE id=r.id;
  ELSE
   PERFORM ai_release_request(r.id,stamp);
   UPDATE ai_requests SET status='Failed',failure_code='AI_PROVIDER_UNREACHABLE',lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
  END IF;
 ELSIF p_action='Unknown' THEN
  UPDATE ai_requests SET status='NeedsReconcile',failure_code='AI_OUTCOME_UNKNOWN',lease_token=NULL,lease_until=NULL,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSIF p_action='Reject' THEN
  IF phase<>'Dispatch' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  PERFORM ai_release_request(r.id,stamp);
  -- The provider may name why it refused the pinned input; anything else stays the generic code. What it said (status, code, message and the
  -- document it could not use) is kept in provider_result for Admin, so a failed request can be traced to one source (BE#188).
  UPDATE ai_requests SET status='Failed',failure_code=CASE WHEN p_input->>'code' IN('AI_SOURCE_HASH_MISMATCH','AI_SOURCE_UNAVAILABLE','AI_REVISION_NOT_PINNED')
   THEN p_input->>'code' ELSE 'AI_PROVIDER_REJECTED' END,
   provider_result=jsonb_build_object('refusal',jsonb_strip_nulls(jsonb_build_object('httpStatus',left(p_input->>'httpStatus',3),'code',left(p_input->>'code',80),
    'message',left(p_input->>'message',500),'documentId',left(p_input->>'documentId',64)))),lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 ELSE -- NotFound: the provider has no record of the stable request id, so nothing was consumed.
  IF phase<>'Lookup' THEN RETURN jsonb_build_object('code','AI_LEASE_LOST','status',409);END IF;
  PERFORM ai_release_request(r.id,stamp);
  UPDATE ai_requests SET status='Failed',failure_code='AI_REQUEST_NOT_RECEIVED',lease_token=NULL,lease_until=NULL,completed_at=stamp,updated_at=stamp,revision=revision+1 WHERE id=r.id;
 END IF;
 SELECT * INTO r FROM ai_requests WHERE id=r.id;
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('requestId',r.id,'status',r.status,'failureCode',r.failure_code));
END $$;

DO $grants$ DECLARE r text;s record;sig text:='ai_worker_gate(text,text,uuid,uuid,jsonb)';BEGIN
 EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO %I',sig,r);END IF;END LOOP;
 SELECT * INTO s FROM ai_refusal_permissions;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
