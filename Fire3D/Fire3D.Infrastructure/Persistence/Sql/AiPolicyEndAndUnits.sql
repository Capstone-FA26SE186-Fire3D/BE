-- #176: AI policies may be ended early, and only units the AI service accepts (tokens, requests) can be configured.
-- 1. A policy row stays immutable except for ending it: setting or moving effective_until earlier (never into the past),
--    or deleting a policy that has not started yet (cancel).
-- 2. Policies with any other unit can never run (the service answers AI_QUOTA_UNIT_UNSUPPORTED): started ones end now,
--    scheduled ones are cancelled. In production this ends TraineeAnswer ce279028-cdd0-49bf-ba09-094151e6551f ('luot-hoi').
--    Billing quota policies are not touched here: issued quotations pin them through payment and provisioning.
-- 3. A Trainee's daily usage counts only requests in the unit of the active TraineeAnswer policy, so changing the unit
--    mid-day neither locks the Trainee out nor lifts the limit.
-- 4. ai_admin_gate gains EndPolicy and rejects other units on CreatePolicy. Every other branch matches AiContractV2.sql.
-- Owner and grants of the gate are unchanged by OR REPLACE; the owner role is held only while the function is replaced.
CREATE TEMP TABLE ai_policy_end_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ai_policy_end_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- 1. Ending is the only allowed update; only policies that have not started may be deleted (no request can have pinned them).
CREATE OR REPLACE FUNCTION immutable_ai_operation_policy() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' AND OLD.effective_from>now() THEN RETURN OLD;END IF;
 IF TG_OP='UPDATE' AND to_jsonb(NEW)-'effective_until'=to_jsonb(OLD)-'effective_until'
    AND NEW.effective_until IS NOT NULL AND NEW.effective_until>=now()
    AND (OLD.effective_until IS NULL OR (OLD.effective_until>now() AND NEW.effective_until<OLD.effective_until)) THEN
  RETURN NEW;
 END IF;
 RAISE EXCEPTION 'AI operation policies are immutable except for ending them' USING ERRCODE='23514';
END $$;
GRANT UPDATE(effective_until),DELETE ON ai_operation_policies TO fet3d_ifc_upload_owner;

-- 2. Unsupported units: started policies end now, scheduled ones are cancelled.
UPDATE ai_operation_policies SET effective_until=now()
 WHERE quota_unit NOT IN('tokens','requests') AND effective_from<now() AND (effective_until IS NULL OR effective_until>now());
DELETE FROM ai_operation_policies WHERE quota_unit NOT IN('tokens','requests') AND effective_from>now();

-- 3. Daily Trainee usage in the active policy's unit (same signature, so owner and grants stay).
CREATE OR REPLACE FUNCTION ai_trainee_units_today(p_actor uuid) RETURNS TABLE(used bigint,reserved bigint,day_start timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 WITH d AS (SELECT (date_trunc('day',clock_timestamp() AT TIME ZONE 'Asia/Ho_Chi_Minh') AT TIME ZONE 'Asia/Ho_Chi_Minh') s),
 p AS (SELECT quota_unit FROM ai_operation_policies WHERE operation='TraineeAnswer' AND effective_from<=clock_timestamp()
   AND (effective_until IS NULL OR effective_until>clock_timestamp()))
 SELECT COALESCE(sum(r.consumed_units) FILTER (WHERE r.status='Succeeded'),0),
  COALESCE(sum(r.reserved_units) FILTER (WHERE r.status IN('Accepted','Processing','NeedsReconcile')),0),d.s
 FROM d LEFT JOIN ai_requests r ON r.audience='trainee' AND r.actor_user_id=p_actor AND r.created_at>=d.s
  AND r.quota_unit=(SELECT quota_unit FROM p) GROUP BY d.s
$$;

-- 4. Admin gate.
CREATE OR REPLACE FUNCTION ai_admin_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;r ai_requests;k knowledge_sources;pol ai_operation_policies;receipt ai_command_receipts;h text;result jsonb;total integer;ends timestamptz;
 page integer:=COALESCE((p_input->>'page')::int,1);size integer:=COALESCE((p_input->>'pageSize')::int,20);stamp timestamptz:=clock_timestamp();
BEGIN
 IF p_action NOT IN('ListRequests','GetRequest','Reconcile','ListPolicies','CreatePolicy','EndPolicy','ListSources','GetSource','CreateSource','ApproveSource','RetireSource') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF page<1 OR size NOT BETWEEN 1 AND 100 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;

 IF p_action='ListRequests' THEN
  IF (p_input->>'status' IS NOT NULL AND p_input->>'status' NOT IN('Accepted','Processing','Succeeded','Failed','NeedsReconcile','Rejected'))
   OR (p_input->>'operation' IS NOT NULL AND p_input->>'operation' NOT IN('ScenarioDraft','Answer','TraineeAnswer'))
   OR (p_input->>'audience' IS NOT NULL AND p_input->>'audience' NOT IN('organization','trainee')) THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  WITH f AS MATERIALIZED(SELECT id,created_at FROM ai_requests WHERE (p_input->>'status' IS NULL OR status=p_input->>'status')
    AND (p_input->>'operation' IS NULL OR operation=p_input->>'operation') AND (p_input->>'audience' IS NULL OR audience=p_input->>'audience')
    AND (p_input->>'organizationId' IS NULL OR organization_id=(p_input->>'organizationId')::uuid))
  SELECT (SELECT count(*) FROM f),COALESCE((SELECT jsonb_agg(ai_request_view(x.id,'admin') ORDER BY x.created_at DESC,x.id)
   FROM (SELECT * FROM f ORDER BY created_at DESC,id OFFSET (page-1)::bigint*size LIMIT size) x),'[]') INTO total,result;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 ELSIF p_action='GetRequest' THEN
  IF NOT EXISTS(SELECT 1 FROM ai_requests WHERE id=p_resource) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',ai_request_view(p_resource,'admin'));
 ELSIF p_action='ListPolicies' THEN
  IF p_input->>'operation' IS NOT NULL AND p_input->>'operation' NOT IN('ScenarioDraft','Answer','TraineeAnswer') THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  SELECT count(*) INTO total FROM ai_operation_policies WHERE p_input->>'operation' IS NULL OR operation=p_input->>'operation';
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.operation,x.effective_from DESC,x.id),'[]') INTO result FROM (SELECT id,operation,effective_from,jsonb_build_object('id',id,'operation',operation,
   'quotaUnit',quota_unit,'reserveUnits',reserve_units,'maxInputChars',max_input_chars,'maxSources',max_sources,'dailyUnits',daily_units,'effectiveFrom',effective_from,
   'effectiveUntil',effective_until,'createdAt',created_at) item FROM ai_operation_policies WHERE p_input->>'operation' IS NULL OR operation=p_input->>'operation'
   ORDER BY operation,effective_from DESC,id OFFSET (page-1)::bigint*size LIMIT size) x;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 ELSIF p_action='ListSources' THEN
  SELECT count(*) INTO total FROM knowledge_sources WHERE p_input->>'status' IS NULL OR approval_status=p_input->>'status';
  SELECT COALESCE(jsonb_agg(x.item ORDER BY x.created_at DESC,x.id),'[]') INTO result FROM (SELECT id,created_at,jsonb_build_object('id',id,'visibility',visibility,'organizationId',organization_id,'title',title,
   'sourceUri',source_uri,'versionLabel',version_label,'sourceHash',source_hash,'jurisdiction',jurisdiction,'effectiveFrom',effective_from,'effectiveUntil',effective_until,'approvalStatus',approval_status,
   'approvedAt',approved_at,'createdAt',created_at,'revision',revision) item FROM knowledge_sources WHERE p_input->>'status' IS NULL OR approval_status=p_input->>'status'
   ORDER BY created_at DESC,id OFFSET (page-1)::bigint*size LIMIT size) x;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('items',result,'total',total,'page',page,'pageSize',size));
 END IF;

 IF p_action IN('CreatePolicy','EndPolicy','CreateSource','Reconcile') THEN
  IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:ai-receipt:'||p_actor||':'||p_key,0));
  SELECT * INTO receipt FROM ai_command_receipts WHERE actor_user_id=p_actor AND operation='Admin'||p_action AND idempotency_key=p_key;
  IF receipt.actor_user_id IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   RETURN jsonb_build_object('code','OK','result',receipt.result,'replayed',true);
  END IF;
 ELSIF p_action IN('ApproveSource','RetireSource') AND p_expected IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428);
 END IF;

 IF p_action='Reconcile' THEN
  -- Scheduling only: the worker looks the stable request id up at the provider; consumed numbers are never edited here.
  SELECT * INTO r FROM ai_requests WHERE id=p_resource FOR UPDATE;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF r.status<>'NeedsReconcile' THEN RETURN jsonb_build_object('code','AI_RECONCILE_NOT_ALLOWED','status',409);END IF;
  UPDATE ai_requests SET reconcile_requested_at=COALESCE(reconcile_requested_at,stamp),reconcile_requested_by=p_actor,updated_at=stamp,revision=revision+1 WHERE id=r.id;
  result:=ai_request_view(r.id,'admin');
 ELSIF p_action IN('CreatePolicy','EndPolicy') THEN
  IF p_action='CreatePolicy' THEN
   -- The AI service settles usage only in these units (AI/rag-api answering/usage.py).
   IF p_input->>'quotaUnit' IS NULL OR p_input->>'quotaUnit' NOT IN('tokens','requests') THEN RETURN jsonb_build_object('code','AI_QUOTA_UNIT_UNSUPPORTED','status',422);END IF;
   BEGIN
    INSERT INTO ai_operation_policies(id,operation,quota_unit,reserve_units,max_input_chars,max_sources,daily_units,effective_from,effective_until,created_by,created_at)
    VALUES(gen_random_uuid(),p_input->>'operation',p_input->>'quotaUnit',(p_input->>'reserveUnits')::int,(p_input->>'maxInputChars')::int,(p_input->>'maxSources')::int,
     (p_input->>'dailyUnits')::int,COALESCE((p_input->>'effectiveFrom')::timestamptz,stamp),(p_input->>'effectiveUntil')::timestamptz,p_actor,stamp) RETURNING * INTO pol;
   EXCEPTION WHEN exclusion_violation THEN RETURN jsonb_build_object('code','AI_POLICY_OVERLAP','status',409);
    WHEN check_violation OR not_null_violation OR invalid_text_representation OR datetime_field_overflow OR numeric_value_out_of_range THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);
   END;
  ELSE
   -- Ends the period at effectiveUntil (default now): only earlier than the current end and never in the past. A policy that has
   -- not started is cancelled (removed) when the end is at or before its start. Requests already accepted keep the policy they pinned.
   SELECT * INTO pol FROM ai_operation_policies WHERE id=p_resource FOR UPDATE;
   IF pol.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
   IF pol.effective_until IS NOT NULL AND pol.effective_until<=stamp THEN RETURN jsonb_build_object('code','AI_POLICY_ENDED','status',409);END IF;
   BEGIN ends:=COALESCE((p_input->>'effectiveUntil')::timestamptz,stamp);
   EXCEPTION WHEN invalid_text_representation OR invalid_datetime_format OR datetime_field_overflow THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);
   END;
   IF ends<now() OR ends>=COALESCE(pol.effective_until,'infinity') OR (ends<=pol.effective_from AND pol.effective_from<=stamp) THEN
    RETURN jsonb_build_object('code','AI_POLICY_END_INVALID','status',422);END IF;
   IF ends<=pol.effective_from THEN DELETE FROM ai_operation_policies WHERE id=pol.id;
   ELSE UPDATE ai_operation_policies SET effective_until=ends WHERE id=pol.id RETURNING * INTO pol;
   END IF;
  END IF;
  result:=jsonb_build_object('id',pol.id,'operation',pol.operation,'quotaUnit',pol.quota_unit,'reserveUnits',pol.reserve_units,'maxInputChars',pol.max_input_chars,'maxSources',pol.max_sources,
   'dailyUnits',pol.daily_units,'effectiveFrom',pol.effective_from,'effectiveUntil',pol.effective_until,'createdAt',pol.created_at);
  IF p_action='EndPolicy' THEN result:=result||jsonb_build_object('cancelled',ends<=pol.effective_from);END IF;
 ELSIF p_action='CreateSource' THEN
  IF p_input->>'visibility'='Organization' AND NOT EXISTS(SELECT 1 FROM organizations WHERE id=(p_input->>'organizationId')::uuid AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  BEGIN
   INSERT INTO knowledge_sources(id,organization_id,visibility,title,source_uri,version_label,source_hash,jurisdiction,effective_from,effective_until,approval_status,created_by,created_at)
   VALUES(gen_random_uuid(),CASE WHEN p_input->>'visibility'='Organization' THEN (p_input->>'organizationId')::uuid END,p_input->>'visibility',btrim(p_input->>'title'),p_input->>'sourceUri',
    p_input->>'versionLabel',lower(p_input->>'sourceHash'),p_input->>'jurisdiction',(p_input->>'effectiveFrom')::timestamptz,(p_input->>'effectiveUntil')::timestamptz,'Draft',p_actor,stamp) RETURNING * INTO k;
  EXCEPTION WHEN unique_violation THEN RETURN jsonb_build_object('code','KNOWLEDGE_SOURCE_EXISTS','status',409);
   WHEN check_violation OR not_null_violation OR invalid_text_representation OR datetime_field_overflow OR string_data_right_truncation THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);
  END;
  result:=jsonb_build_object('id',k.id,'visibility',k.visibility,'organizationId',k.organization_id,'title',k.title,'sourceUri',k.source_uri,'versionLabel',k.version_label,'sourceHash',k.source_hash,
   'jurisdiction',k.jurisdiction,'effectiveFrom',k.effective_from,'effectiveUntil',k.effective_until,'approvalStatus',k.approval_status,'createdAt',k.created_at,'revision',k.revision);
 ELSE -- GetSource, ApproveSource, RetireSource
  SELECT * INTO k FROM knowledge_sources WHERE id=p_resource FOR UPDATE;
  IF k.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF p_action<>'GetSource' THEN
   IF k.revision<>p_expected THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412);END IF;
   IF p_action='ApproveSource' AND k.approval_status<>'Draft' OR p_action='RetireSource' AND k.approval_status<>'Approved' THEN RETURN jsonb_build_object('code','KNOWLEDGE_SOURCE_STATE_CONFLICT','status',409);END IF;
   UPDATE knowledge_sources SET approval_status=CASE WHEN p_action='ApproveSource' THEN 'Approved' ELSE 'Retired' END,
    approved_by=CASE WHEN p_action='ApproveSource' THEN p_actor ELSE approved_by END,approved_at=CASE WHEN p_action='ApproveSource' THEN stamp ELSE approved_at END,revision=revision+1
    WHERE id=k.id RETURNING * INTO k;
   -- Learn citations and AI eligibility read the live state, so retiring a source takes effect without touching published versions.
   PERFORM content_invalidate('knowledge',k.id,k.revision,jsonb_build_object('approvalStatus',k.approval_status));
  END IF;
  result:=jsonb_build_object('id',k.id,'visibility',k.visibility,'organizationId',k.organization_id,'title',k.title,'sourceUri',k.source_uri,'versionLabel',k.version_label,'sourceHash',k.source_hash,
   'jurisdiction',k.jurisdiction,'effectiveFrom',k.effective_from,'effectiveUntil',k.effective_until,'approvalStatus',k.approval_status,'approvedAt',k.approved_at,'createdAt',k.created_at,'revision',k.revision);
  IF p_action='GetSource' THEN RETURN jsonb_build_object('code','OK','result',result);END IF;
 END IF;
 IF p_action IN('CreatePolicy','EndPolicy','CreateSource','Reconcile') THEN INSERT INTO ai_command_receipts VALUES(p_actor,'Admin'||p_action,p_key,h,result,stamp);END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,NULL,'User',(CASE WHEN p_action LIKE 'Create%' THEN 'Create' WHEN (result->>'cancelled')::boolean THEN 'Delete' ELSE 'Update' END)::audit_action_enum,
  CASE WHEN p_action='Reconcile' THEN 'AiRequest' WHEN p_action IN('CreatePolicy','EndPolicy') THEN 'AiOperationPolicy' ELSE 'KnowledgeSource' END,
  (result->>CASE WHEN p_action='Reconcile' THEN 'requestId' ELSE 'id' END)::uuid,gen_random_uuid(),
  jsonb_build_object('operation',p_action)||CASE WHEN p_action='EndPolicy' THEN jsonb_build_object('effectiveUntil',result->'effectiveUntil','cancelled',result->'cancelled') ELSE '{}'::jsonb END,now());
 RETURN jsonb_build_object('code','OK','result',result);
END $$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM ai_policy_end_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
