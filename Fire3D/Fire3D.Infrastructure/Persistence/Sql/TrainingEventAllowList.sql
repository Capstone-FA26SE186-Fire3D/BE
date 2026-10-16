-- SCENARIO-02 (Docs ifc_ingestion_visual_interaction.md 6): learner and playtest events are checked against an allow-list.
-- Allowed: the core events the server scores (ExitReached, WrongExit, HazardExposure, Moved) plus the events declared in
-- capability_contracts[*].events of the runtime the session pinned, with a payload matching that event's parameter schema.
-- Any other event keeps its sequence (acknowledgement and completion never wait for it) but is stored with rejection_code
-- EVENT_TYPE_UNSUPPORTED or EVENT_PAYLOAD_INVALID, returned in conflicts and never used for metrics.
-- Metrics read core event types only, which are never rejected, so scores ignore rejected events while the completion-time
-- fallback (last event) still counts every received event. An Assessment also needs a rubric with a positive total weight.
-- Gate bodies are the ones from AddLearnerSessions / AddPlaytestRuntime with only the event insert, the replay branch and
-- the Assessment rubric check changed.
CREATE TEMP TABLE training_event_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO training_event_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

ALTER TABLE session_events ADD COLUMN rejection_code varchar(64) CHECK(rejection_code IN('EVENT_TYPE_UNSUPPORTED','EVENT_PAYLOAD_INVALID'));
ALTER TABLE playtest_events ADD COLUMN rejection_code varchar(64) CHECK(rejection_code IN('EVENT_TYPE_UNSUPPORTED','EVENT_PAYLOAD_INVALID'));

-- Same closed subset as ParameterSchema.ValidateValue in C#: declared properties only, required present, type/range/length/enum.
-- No schema means the event carries an empty payload.
CREATE FUNCTION capability_payload_valid(p_schema jsonb,p_payload jsonb) RETURNS boolean LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog,public AS $$
DECLARE k text;v jsonb;prop jsonb;t text;n numeric;s text;
BEGIN
 IF jsonb_typeof(p_payload) IS DISTINCT FROM 'object' THEN RETURN false;END IF;
 IF jsonb_typeof(p_schema) IS DISTINCT FROM 'object' THEN RETURN p_payload='{}'::jsonb;END IF;
 IF jsonb_typeof(p_schema->'required')='array' AND EXISTS(SELECT 1 FROM jsonb_array_elements_text(p_schema->'required') r WHERE NOT p_payload ? r) THEN RETURN false;END IF;
 FOR k,v IN SELECT key,value FROM jsonb_each(p_payload) LOOP
  prop:=p_schema->'properties'->k;
  IF jsonb_typeof(prop) IS DISTINCT FROM 'object' THEN RETURN false;END IF;
  t:=prop->>'type';
  IF t IN('number','integer') THEN
   -- C# reads integers as 64-bit (long.TryParse of the token): no fraction or exponent, at most 18 digits.
   IF jsonb_typeof(v)<>'number' OR (t='integer' AND v::text !~ '^-?[0-9]{1,18}$') THEN RETURN false;END IF;
   n:=v::text::numeric;
   IF jsonb_typeof(prop->'minimum')='number' AND n<(prop->>'minimum')::numeric THEN RETURN false;END IF;
   IF jsonb_typeof(prop->'maximum')='number' AND n>(prop->>'maximum')::numeric THEN RETURN false;END IF;
  ELSIF t='string' THEN
   IF jsonb_typeof(v)<>'string' THEN RETURN false;END IF;
   s:=v#>>'{}';
   -- Like C#: not blank (Unicode white space), no control characters, length in UTF-16 code units.
   IF s ~ '^[[:space:]   -     　]*$' OR s ~ '[--]'
    OR length(s)+(SELECT count(*) FROM regexp_matches(s,'[𐀀-􏿿]','g'))>COALESCE((prop->>'maxLength')::int,10000) THEN RETURN false;END IF;
  ELSIF t='boolean' THEN
   IF jsonb_typeof(v)<>'boolean' THEN RETURN false;END IF;
  ELSE RETURN false;END IF;
  -- Enum values compare as JSON text like C# (1.0 is not 1, "1" is not 1).
  IF jsonb_typeof(prop->'enum')='array' AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(prop->'enum') x WHERE x::text=v::text) THEN RETURN false;END IF;
 END LOOP;
 RETURN true;
END $$;

-- NULL when the event is allowed for the runtime, otherwise the rejection code. Catalog rows are immutable per runtime version,
-- so a session pinned to a runtime keeps its allow-list even if that runtime is later deactivated.
CREATE FUNCTION training_event_rejection(p_runtime text,p_type text,p_payload jsonb) RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE spec jsonb;cat runtime_compatibility_catalog;
BEGIN
 IF p_type IN('ExitReached','WrongExit','HazardExposure','Moved') THEN RETURN NULL;END IF;
 -- The same row the editor and the published catalog use for this version (latest active, else latest).
 SELECT * INTO cat FROM runtime_compatibility_catalog WHERE runtime_version=p_runtime ORDER BY is_active DESC,created_at DESC,id LIMIT 1;
 IF cat.id IS NULL THEN RETURN 'EVENT_TYPE_UNSUPPORTED';END IF;
 SELECT ev INTO spec FROM jsonb_each(CASE WHEN jsonb_typeof(cat.capability_contracts)='object' THEN cat.capability_contracts ELSE '{}'::jsonb END) cc(capability,contract)
  CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(cc.contract->'events')='array' THEN cc.contract->'events' ELSE '[]'::jsonb END) ev
  WHERE cat.capabilities ? cc.capability AND ev->>'type'=p_type
  ORDER BY cc.capability COLLATE "C" LIMIT 1;
 IF spec IS NULL THEN RETURN 'EVENT_TYPE_UNSUPPORTED';END IF;
 IF NOT capability_payload_valid(spec->'payload',p_payload) THEN RETURN 'EVENT_PAYLOAD_INVALID';END IF;
 RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION learner_session_gate(p_action text,p_actor uuid,p_family uuid,p_continuation uuid,p_resource uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;t trainings;rel releases;pk release_packages;pin release_build_provenance;cr scenario_content_reviews;v scenario_versions;
 art revision_artifacts;prep training_session_preparations;s sessions;ent service_entitlements;seat billing_learner_seats;receipt learner_command_receipts;
 h text;result jsonb;stamp timestamptz;new_id uuid;lim integer;used integer;e jsonb;existing session_events;accepted jsonb:='[]';duplicates jsonb:='[]';conflicts jsonb:='[]';
 ack jsonb;live boolean;package jsonb;ttl integer;rejected text;
BEGIN
 IF p_action NOT IN('Prepare','Get','Start','Launched','Heartbeat','Events','Complete','Result','Continuation','Reconcile') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 IF p_action IN('Prepare','Start','Complete') AND (p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]') THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND deleted_at IS NULL;
 live:=p_family IS NOT NULL AND fet3d_live_family(p_actor,p_family) AND actor.is_active AND (actor.registration_expires_at IS NULL OR actor.email_verified_at IS NOT NULL);
 -- Continuation proves only ownership of one started session for sync; it never opens or starts anything.
 IF p_continuation IS NOT NULL AND p_action NOT IN('Get','Heartbeat','Events','Complete','Result') THEN RETURN jsonb_build_object('code','CONTINUATION_NOT_ALLOWED','status',403);END IF;
 IF p_continuation IS NULL AND NOT live THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.id IS NULL OR actor.role<>'Trainee' THEN RETURN jsonb_build_object('code',CASE WHEN actor.id IS NULL THEN 'UNAUTHORIZED' ELSE 'FORBIDDEN' END,'status',CASE WHEN actor.id IS NULL THEN 401 ELSE 403 END);END IF;

 IF p_action='Reconcile' THEN
  -- Only the caller's own sessions/preparations/receipts are reported; unknown IDs are simply absent.
  SELECT COALESCE(jsonb_agg(learner_session_state(x.id,p_family)) FILTER(WHERE learner_session_state(x.id,p_family) IS NOT NULL),'[]') INTO result FROM (
   SELECT s2.id FROM sessions s2 WHERE s2.trainee_user_id=p_actor AND s2.contract_version=7 AND s2.id IN(SELECT (jsonb_array_elements_text(COALESCE(p_input->'sessionIds','[]')))::uuid)
   UNION SELECT p2.id FROM training_session_preparations p2 WHERE p2.trainee_user_id=p_actor AND p2.id IN(SELECT (jsonb_array_elements_text(COALESCE(p_input->'sessionIds','[]')))::uuid)
   UNION SELECT (r.result->'session'->>'id')::uuid FROM learner_command_receipts r WHERE r.actor_id=p_actor AND r.operation IN('Prepare','Start')
    AND r.idempotency_key IN(SELECT jsonb_array_elements_text(COALESCE(p_input->'idempotencyKeys','[]')))) x;
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;

 IF p_key IS NOT NULL THEN
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input));
  SELECT * INTO receipt FROM learner_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
 END IF;
 IF p_action='Prepare' AND receipt.result IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
  RETURN jsonb_build_object('code','OK','result',receipt.result);
 END IF;

 IF p_action='Prepare' THEN
  IF p_input->>'mode' NOT IN('Learn','Guided','Assessment') OR p_input->>'mode' IS NULL OR p_input->>'trainingId' !~ '^[0-9a-f-]{36}$' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  SELECT * INTO t FROM trainings WHERE id=(p_input->>'trainingId')::uuid;
  SELECT * INTO rel FROM releases WHERE id=t.release_id;
  SELECT * INTO b FROM buildings WHERE id=rel.building_id;
  IF t.id IS NULL OR b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF b.visibility<>'Public' AND NOT EXISTS(SELECT 1 FROM building_participation_grants WHERE building_id=b.id AND trainee_user_id=p_actor AND access_revision=b.access_revision) THEN RETURN jsonb_build_object('code','BUILDING_ACCESS_REQUIRED','status',403);END IF;
  IF t.status<>'Active' OR rel.status<>'Published' OR (t.start_date IS NOT NULL AND t.start_date>clock_timestamp()) OR (t.end_date IS NOT NULL AND t.end_date<=clock_timestamp()) THEN RETURN jsonb_build_object('code','TRAINING_UNAVAILABLE','status',409);END IF;
  IF NOT (p_input->>'mode')=ANY(t.allowed_modes) THEN RETURN jsonb_build_object('code','MODE_NOT_ALLOWED','status',409);END IF;
  SELECT * INTO pin FROM release_build_provenance WHERE release_id=rel.id;
  SELECT * INTO cr FROM scenario_content_reviews WHERE id=pin.content_review_id AND status='Approved';
  SELECT * INTO v FROM scenario_versions WHERE id=t.scenario_version_id;
  SELECT * INTO pk FROM release_packages WHERE release_id=rel.id;
  SELECT * INTO art FROM revision_artifacts WHERE id=pk.candidate_artifact_id;
  IF pin.release_id IS NULL OR cr.id IS NULL OR pk.id IS NULL OR art.id IS NULL THEN RETURN jsonb_build_object('code','CONTENT_APPROVAL_REQUIRED','status',409);END IF;
  IF package_runtime_compatible(art.metadata,p_input->>'runtimeVersion') IS NOT TRUE THEN RETURN jsonb_build_object('code','RUNTIME_INCOMPATIBLE','status',409);END IF;
  IF p_input->>'mode'='Assessment' AND (jsonb_typeof(v.rubric->'criteria') IS DISTINCT FROM 'array' OR EXISTS(SELECT 1 FROM jsonb_array_elements(v.rubric->'criteria') c
   WHERE NOT (c->>'metric')=ANY(ARRAY['reached_exit','completion_time_seconds','wrong_exits','hazard_exposure','distance_meters']))) THEN
   RETURN jsonb_build_object('code','RUBRIC_NOT_SUPPORTED','status',409);END IF;
  -- An Assessment must be scorable: at least one criterion and a positive total weight (otherwise the result could only be NotAssessed).
  IF p_input->>'mode'='Assessment' AND COALESCE((SELECT sum(CASE WHEN jsonb_typeof(c->'weight')='number' THEN (c->>'weight')::numeric ELSE 0 END)
   FROM jsonb_array_elements(v.rubric->'criteria') c),0)<=0 THEN RETURN jsonb_build_object('code','RUBRIC_NOT_SUPPORTED','status',409);END IF;
  package:=jsonb_build_object('packageId',pk.id,'artifactId',art.id,'manifestArtifactId',pin.manifest_artifact_id,'validationRunId',pin.validation_run_id,
   'packageHash',pk.checksum_sha256,'manifestHash',pk.manifest_sha256,'buildTarget',pk.build_target,'minRuntimeVersion',art.metadata->>'minRuntimeVersion',
   'protocolVersion',art.metadata->>'protocolVersion','manifestSchemaVersion',art.metadata->>'manifestSchemaVersion','requiredCapabilities',art.metadata->'requiredCapabilities',
   'packageKey',pk.package_url,'manifestKey',pk.manifest_url,'contentReviewId',cr.id);
  new_id:=gen_random_uuid();stamp:=clock_timestamp();
  INSERT INTO training_session_preparations(id,trainee_user_id,organization_id,building_id,training_id,release_id,scenario_version_id,mode,scenario_hash,rubric_hash,package,runtime_version,status,created_at,expires_at)
  VALUES(new_id,p_actor,b.organization_id,b.id,t.id,rel.id,v.id,p_input->>'mode',v.scenario_hash,CASE WHEN v.rubric IS NULL THEN NULL ELSE fet3d_jsonb_payload_hash(v.rubric) END,package,p_input->>'runtimeVersion','Prepared',stamp,stamp+interval '24 hours');
  result:=jsonb_build_object('session',learner_session_state(new_id,p_family));
  INSERT INTO learner_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;

 -- Everything else addresses one preparation or started session owned by the caller.
 SELECT * INTO s FROM sessions WHERE id=p_resource AND trainee_user_id=p_actor AND contract_version=7;
 IF s.id IS NULL THEN SELECT * INTO prep FROM training_session_preparations WHERE id=p_resource AND trainee_user_id=p_actor;END IF;
 IF s.id IS NULL AND prep.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_continuation IS NOT NULL AND (s.id IS NULL OR s.continuation_id IS DISTINCT FROM p_continuation OR s.continuation_expires_at<=clock_timestamp()) THEN RETURN jsonb_build_object('code','CONTINUATION_INVALID','status',401);END IF;

 IF receipt.result IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
  IF p_action='Start' AND s.id IS NOT NULL THEN
   -- A replayed start hands back the original grant only while the start conditions still hold.
   result:=learner_start_recheck(p_actor,p_family,s.building_id,s.training_id,s.release_id,s.package,s.runtime_version);
   IF result IS NOT NULL THEN RETURN result;END IF;
  END IF;
  IF p_action='Complete' AND receipt.result->>'status'='AwaitingSync' THEN
   -- Retried completion: finalize if the missing events have arrived since.
   SELECT * INTO s FROM sessions WHERE id=p_resource AND trainee_user_id=p_actor FOR UPDATE;
   IF s.status='Running' AND s.completion_requested_at IS NOT NULL AND (learner_session_ack(s.id)->>'acknowledgedSequence')::bigint>=s.completion_last_sequence THEN PERFORM learner_session_finalize(s.id);END IF;
   RETURN jsonb_build_object('code','OK','status',CASE WHEN EXISTS(SELECT 1 FROM session_results WHERE session_id=p_resource) THEN 200 ELSE 202 END,
    'result',jsonb_build_object('status',learner_session_state(p_resource,p_family)->>'status','session',learner_session_state(p_resource,p_family))||learner_session_ack(p_resource));
  END IF;
  RETURN jsonb_build_object('code','OK','result',receipt.result);
 END IF;

 IF p_action='Get' THEN RETURN jsonb_build_object('code','OK','result',learner_session_state(p_resource,p_family));END IF;
 IF p_action='Result' THEN
  IF NOT EXISTS(SELECT 1 FROM session_results WHERE session_id=p_resource) THEN RETURN jsonb_build_object('code','RESULT_NOT_READY','status',409);END IF;
  RETURN jsonb_build_object('code','OK','result',(SELECT jsonb_build_object('sessionId',r.session_id,'mode',r.mode,'outcome',r.outcome,'reason',r.outcome_reason,'scorePercent',r.score,
   'rubricHash',r.rubric_hash,'criteria',r.criterion_results,'metrics',r.metrics,'lastEventSequence',r.last_event_sequence,'createdAt',r.created_at) FROM session_results r WHERE r.session_id=p_resource));
 END IF;

 IF p_action='Start' THEN
  IF s.id IS NOT NULL THEN
   -- Same launching session before launch: reissue the grant (new generation); never a second play or seat.
   SELECT * INTO s FROM sessions WHERE id=s.id FOR UPDATE;
   IF s.status<>'Launching' THEN RETURN jsonb_build_object('code','SESSION_ALREADY_LAUNCHED','status',409);END IF;
   IF s.launch_family<>p_family THEN RETURN jsonb_build_object('code','SESSION_MISMATCH','status',409);END IF;
   result:=learner_start_recheck(p_actor,p_family,s.building_id,s.training_id,s.release_id,s.package,s.runtime_version);
   IF result IS NOT NULL THEN RETURN result;END IF;
   stamp:=clock_timestamp();
   IF NOT EXISTS(SELECT 1 FROM service_entitlements WHERE id=s.entitlement_id AND status='Active' AND starts_at<=stamp AND ends_at>stamp) THEN RETURN jsonb_build_object('code','BUILDING_ENTITLEMENT_REQUIRED','status',409);END IF;
   UPDATE sessions SET grant_generation=grant_generation+1,grant_issued_at=stamp,grant_expires_at=stamp+interval '5 minutes' WHERE id=s.id RETURNING * INTO s;
   result:=jsonb_build_object('session',learner_session_state(s.id,p_family),'launch',jsonb_build_object('sessionId',s.id,'traineeId',p_actor,'familyId',p_family,'generation',s.grant_generation,
    'issuedAt',s.grant_issued_at,'expiresAt',s.grant_expires_at,'package',s.package,'runtimeVersion',s.runtime_version,'mode',s.mode),'continuation',jsonb_build_object('id',s.continuation_id,'expiresAt',s.continuation_expires_at));
   INSERT INTO learner_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);
   RETURN jsonb_build_object('code','OK','result',result);
  END IF;
  SELECT * INTO prep FROM training_session_preparations WHERE id=prep.id FOR UPDATE;
  IF prep.status<>'Prepared' THEN RETURN jsonb_build_object('code','SESSION_ALREADY_STARTED','status',409);END IF;
  IF prep.expires_at<=clock_timestamp() THEN RETURN jsonb_build_object('code','PREPARATION_EXPIRED','status',409);END IF;
  SELECT * INTO b FROM buildings WHERE id=prep.building_id;
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
  SELECT * INTO b FROM buildings WHERE id=prep.building_id;
  SELECT * INTO t FROM trainings WHERE id=prep.training_id;SELECT * INTO rel FROM releases WHERE id=prep.release_id;
  -- Online start rechecks live session, lifecycle, access, approval, Training/release state, entitlement and runtime.
  result:=learner_start_recheck(p_actor,p_family,b.id,t.id,rel.id,prep.package,prep.runtime_version);
  IF result IS NOT NULL THEN RETURN result;END IF;
  stamp:=clock_timestamp();
  SELECT e2.* INTO ent FROM service_entitlements e2 WHERE e2.building_id=b.id AND e2.organization_id=b.organization_id AND e2.status='Active' AND e2.payment_transaction_id IS NOT NULL
   AND e2.starts_at<=stamp AND e2.ends_at>stamp ORDER BY e2.starts_at DESC,e2.id LIMIT 1 FOR UPDATE;
  IF ent.id IS NULL THEN RETURN jsonb_build_object('code','BUILDING_ENTITLEMENT_REQUIRED','status',409);END IF;
  -- Seat: one per Trainee per entitlement period, allocated under the entitlement row lock.
  SELECT * INTO seat FROM billing_learner_seats WHERE entitlement_id=ent.id AND trainee_id=p_actor;
  IF seat.id IS NULL THEN
   lim:=billing_effective_learner_limit(ent.id,stamp);
   SELECT count(*) INTO used FROM billing_learner_seats WHERE entitlement_id=ent.id;
   IF lim IS NOT NULL AND used>=lim THEN RETURN jsonb_build_object('code','LEARNER_LIMIT_REACHED','status',409);END IF;
   INSERT INTO billing_learner_seats(entitlement_id,organization_id,building_id,trainee_id,first_session_id,allocated_at) VALUES(ent.id,b.organization_id,b.id,p_actor,prep.id,stamp) RETURNING * INTO seat;
  END IF;
  stamp:=clock_timestamp();ttl:=COALESCE((p_input->>'continuationTtlSeconds')::int,604800);
  INSERT INTO sessions(id,training_id,release_id,scenario_version_id,organization_id,trainee_user_id,start_key,protocol_version,scenario_hash,release_hash,started_at,
   contract_version,building_id,entitlement_id,seat_id,mode,status,rubric_hash,package,runtime_version,launch_family,grant_generation,grant_issued_at,grant_expires_at,continuation_id,continuation_expires_at)
  VALUES(prep.id,prep.training_id,prep.release_id,prep.scenario_version_id,prep.organization_id,p_actor,prep.id,COALESCE(prep.package->>'protocolVersion','1.0'),prep.scenario_hash,prep.package->>'packageHash',stamp,
   7,b.id,ent.id,seat.id,prep.mode::session_mode_enum,'Launching',prep.rubric_hash,prep.package,prep.runtime_version,p_family,1,stamp,stamp+interval '5 minutes',gen_random_uuid(),stamp+make_interval(secs=>ttl)) RETURNING * INTO s;
  UPDATE training_session_preparations SET status='Started',started_at=stamp WHERE id=prep.id;
  result:=jsonb_build_object('session',learner_session_state(s.id,p_family),'launch',jsonb_build_object('sessionId',s.id,'traineeId',p_actor,'familyId',p_family,'generation',1,
   'issuedAt',s.grant_issued_at,'expiresAt',s.grant_expires_at,'package',s.package,'runtimeVersion',s.runtime_version,'mode',s.mode),'continuation',jsonb_build_object('id',s.continuation_id,'expiresAt',s.continuation_expires_at));
  INSERT INTO learner_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create','Session',s.id,gen_random_uuid(),jsonb_build_object('operation','Start','entitlementId',ent.id,'seatId',seat.id,'mode',s.mode),now());
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;

 IF s.id IS NULL THEN RETURN jsonb_build_object('code','SESSION_NOT_STARTED','status',409);END IF;
 SELECT * INTO s FROM sessions WHERE id=s.id FOR UPDATE;

 IF p_action='Continuation' THEN
  stamp:=clock_timestamp();ttl:=COALESCE((p_input->>'continuationTtlSeconds')::int,604800);
  UPDATE sessions SET continuation_id=gen_random_uuid(),continuation_expires_at=stamp+make_interval(secs=>ttl) WHERE id=s.id RETURNING * INTO s;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('id',s.continuation_id,'expiresAt',s.continuation_expires_at,'sessionId',s.id));
 END IF;
 IF p_action='Launched' THEN
  IF s.launch_family<>p_family THEN RETURN jsonb_build_object('code','SESSION_MISMATCH','status',409);END IF;
  IF s.status='Running' AND (p_input->>'generation')::int=s.grant_generation THEN RETURN jsonb_build_object('code','OK','result',learner_session_state(s.id,p_family));END IF;
  IF s.status<>'Launching' THEN RETURN jsonb_build_object('code','SESSION_NOT_LAUNCHING','status',409);END IF;
  IF jsonb_typeof(p_input->'generation') IS DISTINCT FROM 'number' OR (p_input->>'generation')::int<>s.grant_generation THEN RETURN jsonb_build_object('code','LAUNCH_GRANT_STALE','status',409);END IF;
  IF s.grant_expires_at<=clock_timestamp() THEN RETURN jsonb_build_object('code','LAUNCH_GRANT_EXPIRED','status',409);END IF;
  UPDATE sessions SET status='Running',launched_at=clock_timestamp(),last_heartbeat_at=clock_timestamp() WHERE id=s.id;
  RETURN jsonb_build_object('code','OK','result',learner_session_state(s.id,p_family));
 END IF;
 IF s.status='Completed' THEN RETURN jsonb_build_object('code','SESSION_TERMINAL','status',409);END IF;
 IF s.status='Launching' THEN RETURN jsonb_build_object('code','SESSION_NOT_LAUNCHED','status',409);END IF;
 IF p_action='Heartbeat' THEN
  UPDATE sessions SET last_heartbeat_at=clock_timestamp() WHERE id=s.id;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('serverTime',clock_timestamp(),'status',s.status));
 ELSIF p_action='Events' THEN
  IF jsonb_typeof(p_input->'events') IS DISTINCT FROM 'array' OR jsonb_array_length(p_input->'events') NOT BETWEEN 1 AND 500 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);END IF;
  FOR e IN SELECT value FROM jsonb_array_elements(p_input->'events') LOOP
   h:=fet3d_jsonb_payload_hash(e);
   SELECT * INTO existing FROM session_events WHERE session_id=s.id AND client_event_id=(e->>'eventId')::uuid;
   IF existing.id IS NOT NULL THEN
    IF existing.payload_hash<>h THEN conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_HASH_CONFLICT');
    -- A resent rejected event gets the same rejection again; it is never turned into an accepted one.
    ELSIF existing.rejection_code IS NOT NULL THEN conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code',existing.rejection_code);
    ELSE duplicates:=duplicates||to_jsonb(e->>'eventId');END IF;
   ELSIF EXISTS(SELECT 1 FROM session_events WHERE session_id=s.id AND sequence_number=(e->>'sequence')::bigint) THEN
    conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_SEQUENCE_CONFLICT');
   ELSIF s.completion_last_sequence IS NOT NULL AND (e->>'sequence')::bigint>s.completion_last_sequence THEN
    conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_AFTER_COMPLETION');
   ELSE
    -- An event outside the pinned runtime's allow-list keeps its sequence (sync and completion never stall) but is marked rejected.
    rejected:=training_event_rejection(s.runtime_version,e->>'type',e->'payload');
    INSERT INTO session_events(id,session_id,sequence_number,client_event_id,schema_version,event_type,event_data,elapsed_ms,recorded_at,received_at,payload_hash,rejection_code)
    VALUES(gen_random_uuid(),s.id,(e->>'sequence')::bigint,(e->>'eventId')::uuid,e->>'schemaVersion',e->>'type',e->'payload',(e->>'elapsedMs')::bigint,(e->>'occurredAt')::timestamptz,clock_timestamp(),h,rejected);
    IF rejected IS NULL THEN accepted:=accepted||to_jsonb(e->>'eventId');ELSE conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code',rejected);END IF;
   END IF;
  END LOOP;
  ack:=learner_session_ack(s.id);
  IF s.completion_requested_at IS NOT NULL AND (ack->>'acknowledgedSequence')::bigint>=s.completion_last_sequence THEN PERFORM learner_session_finalize(s.id);END IF;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('accepted',accepted,'duplicates',duplicates,'conflicts',conflicts,'status',learner_session_state(s.id,p_family)->>'status')||ack);
 ELSIF p_action='Complete' THEN
  IF s.completion_requested_at IS NOT NULL THEN RETURN jsonb_build_object('code','COMPLETION_ALREADY_REQUESTED','status',409);END IF;
  IF jsonb_typeof(p_input->'lastEventSequence') IS DISTINCT FROM 'number' OR (p_input->>'lastEventSequence') !~ '^[0-9]{1,18}$'
   OR p_input->>'endReason' NOT IN('Finished','Abandoned','TimedOut') OR p_input->>'endReason' IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);END IF;
  IF EXISTS(SELECT 1 FROM session_events WHERE session_id=s.id AND sequence_number>(p_input->>'lastEventSequence')::bigint) THEN RETURN jsonb_build_object('code','EVENTS_BEYOND_COMPLETION','status',409);END IF;
  UPDATE sessions SET completion_key=p_key,completion_hash=h,completion_last_sequence=(p_input->>'lastEventSequence')::bigint,completion_end_reason=p_input->>'endReason',
   completion_requested_at=clock_timestamp() WHERE id=s.id;
  ack:=learner_session_ack(s.id);
  IF (ack->>'acknowledgedSequence')::bigint>=(p_input->>'lastEventSequence')::bigint THEN PERFORM learner_session_finalize(s.id);END IF;
  result:=jsonb_build_object('status',learner_session_state(s.id,p_family)->>'status','session',learner_session_state(s.id,p_family))||ack;
  INSERT INTO learner_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);
  RETURN jsonb_build_object('code','OK','status',CASE WHEN result->>'status'='Completed' THEN 200 ELSE 202 END,'result',result);
 END IF;
 RETURN jsonb_build_object('code','ACTION_INVALID','status',400);
END $$;

CREATE OR REPLACE FUNCTION playtest_runtime_gate(p_action text,p_actor uuid,p_family uuid,p_playtest uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;session playtest_sessions;pin playtest_package_pins;rs playtest_runtime_state;ho playtest_handoffs;receipt playtest_command_receipts;
 ent service_entitlements;h text;result jsonb;stamp timestamptz;e jsonb;n bigint;accepted jsonb:='[]';duplicates jsonb:='[]';conflicts jsonb:='[]';missing jsonb;ack bigint;existing playtest_events;rejected text;
BEGIN
 IF p_action NOT IN('Get','Handoff','Redeem','Reissue','Launched','Heartbeat','Events','Complete','Cancel') THEN RETURN jsonb_build_object('code','PLAYTEST_ACTION_INVALID','status',400);END IF;
 IF p_action IN('Handoff','Reissue','Complete','Cancel') AND (p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]') THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='Redeem' THEN
  IF p_input->>'codeHash' IS NULL OR p_input->>'codeHash' !~ '^[0-9a-f]{64}$' THEN RETURN jsonb_build_object('code','PLAYTEST_HANDOFF_INVALID','status',404);END IF;
  SELECT * INTO ho FROM playtest_handoffs WHERE code_hash=p_input->>'codeHash';
  -- Another user's code is indistinguishable from an unknown code.
  IF ho.id IS NULL OR ho.created_by<>p_actor THEN RETURN jsonb_build_object('code','PLAYTEST_HANDOFF_INVALID','status',404);END IF;
  p_playtest:=ho.playtest_id;
 END IF;
 SELECT * INTO session FROM playtest_sessions WHERE id=p_playtest AND created_by=p_actor;
 SELECT * INTO b FROM buildings WHERE id=session.building_id;
 IF session.id IS NULL OR b.id IS NULL OR b.organization_id IS DISTINCT FROM actor.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL
  OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_action='Get' THEN RETURN jsonb_build_object('code','OK','result',playtest_state(session.id,p_family));END IF;
 -- Serialise every mutation of one playtest (complete/cancel races, redeem races, grant generations).
 SELECT * INTO session FROM playtest_sessions WHERE id=session.id FOR UPDATE;
 SELECT * INTO pin FROM playtest_package_pins WHERE playtest_id=session.id;
 SELECT * INTO rs FROM playtest_runtime_state WHERE playtest_id=session.id FOR UPDATE;
 IF pin.playtest_id IS NULL OR rs.playtest_id IS NULL THEN RETURN jsonb_build_object('code','PLAYTEST_SESSION_MISMATCH','status',409);END IF;
 -- Same lock order as handoff rotation, cancel and start: playtest row first, then its handoff row.
 IF p_action='Redeem' THEN SELECT * INTO ho FROM playtest_handoffs WHERE id=ho.id FOR UPDATE;END IF;
 IF NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 stamp:=clock_timestamp();
 IF p_key IS NOT NULL AND p_action IN('Handoff','Reissue','Complete','Cancel') THEN
  -- Receipts bind actor/operation/resource/canonical input; the session family is checked live, not hashed.
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',session.id,'input',p_input-'codeHash'-'codeCiphertext'));
  SELECT * INTO receipt FROM playtest_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   IF p_action IN('Reissue','Complete') AND pin.started_family IS DISTINCT FROM p_family THEN RETURN jsonb_build_object('code','PLAYTEST_SESSION_MISMATCH','status',409);END IF;
   IF p_action='Handoff' THEN
    SELECT * INTO ho FROM playtest_handoffs WHERE id=(receipt.result->>'handoffId')::uuid;
    -- Only the issuing session may see the code again.
    IF ho.issuing_family IS DISTINCT FROM p_family THEN RETURN jsonb_build_object('code','PLAYTEST_SESSION_MISMATCH','status',409);END IF;
    IF ho.revoked_at IS NOT NULL OR ho.redeemed_at IS NOT NULL OR ho.expires_at<=stamp OR ho.code_ciphertext IS NULL THEN RETURN jsonb_build_object('code','PLAYTEST_HANDOFF_EXPIRED','status',409);END IF;
    RETURN receipt.result||jsonb_build_object('codeCiphertext',encode(ho.code_ciphertext,'base64'));
   END IF;
   IF p_action='Reissue' AND ((receipt.result->>'expiresAt')::timestamptz<=stamp OR rs.grant_generation<>(receipt.result->>'generation')::int) THEN RETURN jsonb_build_object('code','PLAYTEST_GRANT_EXPIRED','status',409);END IF;
   RETURN receipt.result;
  END IF;
 END IF;
 IF p_action='Handoff' THEN
  IF session.status<>'Created' THEN RETURN jsonb_build_object('code','PLAYTEST_ALREADY_STARTED','status',409);END IF;
  IF p_input->>'codeHash' !~ '^[0-9a-f]{64}$' OR p_input->>'codeCiphertext' IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  -- Rotation: a new code invalidates every open code of this playtest.
  UPDATE playtest_handoffs SET revoked_at=stamp,code_ciphertext=NULL WHERE playtest_id=session.id AND revoked_at IS NULL AND redeemed_at IS NULL;
  INSERT INTO playtest_handoffs(id,playtest_id,code_hash,code_ciphertext,issuing_family,created_by,created_at,expires_at)
   VALUES(gen_random_uuid(),session.id,p_input->>'codeHash',decode(p_input->>'codeCiphertext','base64'),p_family,p_actor,stamp,stamp+interval '5 minutes') RETURNING * INTO ho;
  result:=jsonb_build_object('code','OK','handoffId',ho.id,'playtestId',session.id,'expiresAt',ho.expires_at);
 ELSIF p_action='Redeem' THEN
  IF ho.redeemed_at IS NOT NULL THEN
   -- Replay for the same Mobile session only; a code never transfers to a second session.
   IF ho.redeemed_family=p_family AND rs.bound_family=p_family THEN RETURN jsonb_build_object('code','OK','result',playtest_state(session.id,p_family));END IF;
   RETURN jsonb_build_object('code','PLAYTEST_HANDOFF_USED','status',409);
  END IF;
  IF ho.revoked_at IS NOT NULL THEN RETURN jsonb_build_object('code','PLAYTEST_HANDOFF_REVOKED','status',409);END IF;
  IF ho.expires_at<=stamp THEN RETURN jsonb_build_object('code','PLAYTEST_HANDOFF_EXPIRED','status',409);END IF;
  IF session.status<>'Created' THEN RETURN jsonb_build_object('code','PLAYTEST_ALREADY_STARTED','status',409);END IF;
  IF NOT fet3d_live_family(p_actor,ho.issuing_family) THEN RETURN jsonb_build_object('code','PLAYTEST_HANDOFF_ISSUER_INVALID','status',409);END IF;
  UPDATE playtest_handoffs SET redeemed_at=stamp,redeemed_family=p_family,code_ciphertext=NULL WHERE id=ho.id;
  UPDATE playtest_handoffs SET revoked_at=stamp,code_ciphertext=NULL WHERE playtest_id=session.id AND id<>ho.id AND revoked_at IS NULL AND redeemed_at IS NULL;
  UPDATE playtest_runtime_state SET bound_family=p_family,bound_at=stamp WHERE playtest_id=session.id;
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','PlaytestSession',session.id,gen_random_uuid(),jsonb_build_object('operation','RedeemHandoff','handoffId',ho.id),now());
  RETURN jsonb_build_object('code','OK','result',playtest_state(session.id,p_family));
 ELSIF p_action='Reissue' THEN
  IF session.status<>'Launching' THEN RETURN jsonb_build_object('code',CASE WHEN session.status='Created' THEN 'PLAYTEST_NOT_STARTED' ELSE 'PLAYTEST_ALREADY_LAUNCHED' END,'status',409);END IF;
  IF pin.started_family<>p_family THEN RETURN jsonb_build_object('code','PLAYTEST_SESSION_MISMATCH','status',409);END IF;
  IF package_runtime_compatible(pin.snapshot->'metadata',session.runtime_version) IS NOT TRUE THEN RETURN jsonb_build_object('code','RUNTIME_INCOMPATIBLE','status',409);END IF;
  IF NOT EXISTS(SELECT 1 FROM processing_job_attempts pa JOIN processing_jobs j ON j.id=pa.processing_job_id AND j.current_attempt_id=pa.id JOIN revision_artifacts art ON art.id=pin.artifact_id AND art.attempt_id=pa.id WHERE pa.id=pin.attempt_id AND pa.status='Succeeded' AND j.status='Succeeded' AND art.is_runtime_ready) THEN RETURN jsonb_build_object('code','PLAYTEST_PACKAGE_REQUIRED','status',409);END IF;
  -- Same entitlement condition as start, without consuming another Trial unit.
  SELECT * INTO ent FROM service_entitlements WHERE id=session.service_entitlement_id AND organization_id=b.organization_id AND building_id=b.id AND status IN('Active','Trial') AND starts_at<=stamp AND ends_at>stamp;
  IF ent.id IS NULL THEN RETURN jsonb_build_object('code','PLAYTEST_ENTITLEMENT_REQUIRED','status',409);END IF;
  stamp:=clock_timestamp();
  UPDATE playtest_runtime_state SET grant_generation=grant_generation+1,grant_issued_at=stamp,grant_expires_at=stamp+interval '5 minutes' WHERE playtest_id=session.id RETURNING * INTO rs;
  result:=jsonb_build_object('code','OK','playtestId',session.id,'actorId',p_actor,'familyId',p_family,'scenarioVersionId',session.scenario_version_id,'artifactId',pin.artifact_id,'validationRunId',pin.validation_run_id,'packageHash',pin.snapshot->>'packageHash','manifestHash',pin.snapshot->>'manifestHash','buildTarget',pin.snapshot->>'buildTarget','packageKey',pin.snapshot->>'packageKey','manifestKey',pin.snapshot->>'manifestKey','protocolVersion',session.protocol_version,'manifestSchemaVersion',session.manifest_schema_version,'runtimeVersion',session.runtime_version,'issuedAt',stamp,'expiresAt',stamp+interval '5 minutes','generation',rs.grant_generation);
 ELSIF p_action='Launched' THEN
  IF pin.started_family IS DISTINCT FROM p_family THEN RETURN jsonb_build_object('code','PLAYTEST_SESSION_MISMATCH','status',409);END IF;
  IF session.status='Running' AND rs.launched_generation=(p_input->>'generation')::int THEN RETURN jsonb_build_object('code','OK','result',playtest_state(session.id,p_family));END IF;
  IF session.status<>'Launching' THEN RETURN jsonb_build_object('code',CASE WHEN session.status='Created' THEN 'PLAYTEST_NOT_STARTED' ELSE 'PLAYTEST_NOT_LAUNCHING' END,'status',409);END IF;
  IF jsonb_typeof(p_input->'generation') IS DISTINCT FROM 'number' OR (p_input->>'generation')::int<>rs.grant_generation THEN RETURN jsonb_build_object('code','PLAYTEST_GRANT_STALE','status',409);END IF;
  IF rs.grant_expires_at<=stamp THEN RETURN jsonb_build_object('code','PLAYTEST_GRANT_EXPIRED','status',409);END IF;
  UPDATE playtest_sessions SET status='Running' WHERE id=session.id;
  UPDATE playtest_runtime_state SET launched_generation=rs.grant_generation,launched_at=stamp,last_heartbeat_at=stamp WHERE playtest_id=session.id;
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','PlaytestSession',session.id,gen_random_uuid(),jsonb_build_object('operation','Launched','generation',rs.grant_generation),now());
  RETURN jsonb_build_object('code','OK','result',playtest_state(session.id,p_family));
 ELSIF p_action IN('Heartbeat','Events','Complete') THEN
  -- After launch only the launching session may sync; a started playtest never moves to another device.
  IF pin.started_family IS DISTINCT FROM p_family THEN RETURN jsonb_build_object('code','PLAYTEST_SESSION_MISMATCH','status',409);END IF;
  IF rs.terminal_status IS NOT NULL AND p_action<>'Complete' THEN RETURN jsonb_build_object('code','PLAYTEST_TERMINAL','status',409,'terminalStatus',rs.terminal_status);END IF;
  IF rs.terminal_status='Cancelled' THEN RETURN jsonb_build_object('code','PLAYTEST_TERMINAL','status',409,'terminalStatus',rs.terminal_status);END IF;
  IF rs.terminal_status IS NULL AND session.status<>'Running' THEN RETURN jsonb_build_object('code','PLAYTEST_NOT_LAUNCHED','status',409);END IF;
  IF p_action='Heartbeat' THEN
   UPDATE playtest_runtime_state SET last_heartbeat_at=stamp WHERE playtest_id=session.id;
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('serverTime',stamp,'status',session.status));
  ELSIF p_action='Events' THEN
   IF jsonb_typeof(p_input->'events') IS DISTINCT FROM 'array' OR jsonb_array_length(p_input->'events') NOT BETWEEN 1 AND 500 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);END IF;
   FOR e IN SELECT value FROM jsonb_array_elements(p_input->'events') LOOP
    IF e->>'eventId' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' OR jsonb_typeof(e->'sequence') IS DISTINCT FROM 'number' OR (e->>'sequence') !~ '^[1-9][0-9]{0,17}$'
     OR NULLIF(e->>'schemaVersion','') IS NULL OR length(e->>'schemaVersion')>32 OR NULLIF(e->>'type','') IS NULL OR length(e->>'type')>100 OR jsonb_typeof(e->'payload') IS DISTINCT FROM 'object'
     OR (e ? 'occurredAt' AND jsonb_typeof(e->'occurredAt') IS DISTINCT FROM 'string') THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);END IF;
   END LOOP;
   FOR e IN SELECT value FROM jsonb_array_elements(p_input->'events') LOOP
    h:=fet3d_jsonb_payload_hash(e-'receivedAt');
    SELECT * INTO existing FROM playtest_events WHERE playtest_id=session.id AND event_id=(e->>'eventId')::uuid;
    IF existing.event_id IS NOT NULL THEN
     IF existing.payload_hash<>h THEN conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_HASH_CONFLICT');
     ELSIF existing.rejection_code IS NOT NULL THEN conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code',existing.rejection_code);
     ELSE duplicates:=duplicates||to_jsonb(e->>'eventId');END IF;
    ELSIF EXISTS(SELECT 1 FROM playtest_events WHERE playtest_id=session.id AND sequence=(e->>'sequence')::bigint) THEN
     conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_SEQUENCE_CONFLICT');
    ELSE
     rejected:=training_event_rejection(session.runtime_version,e->>'type',e->'payload');
     INSERT INTO playtest_events(playtest_id,event_id,sequence,schema_version,event_type,occurred_at,payload,payload_hash,received_at,rejection_code)
      VALUES(session.id,(e->>'eventId')::uuid,(e->>'sequence')::bigint,e->>'schemaVersion',e->>'type',(e->>'occurredAt')::timestamptz,e->'payload',h,stamp,rejected);
     IF rejected IS NULL THEN accepted:=accepted||to_jsonb(e->>'eventId');ELSE conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code',rejected);END IF;
    END IF;
   END LOOP;
   SELECT COALESCE(max(sequence),0) INTO ack FROM (SELECT sequence,row_number() OVER(ORDER BY sequence) rn FROM playtest_events WHERE playtest_id=session.id) x WHERE sequence=rn;
   SELECT COALESCE(jsonb_agg(jsonb_build_object('from',gap_start,'to',gap_end) ORDER BY gap_start),'[]') INTO missing FROM (
    SELECT COALESCE(lag(sequence) OVER(ORDER BY sequence),0)+1 gap_start,sequence-1 gap_end FROM playtest_events WHERE playtest_id=session.id) g WHERE gap_end>=gap_start;
   RETURN jsonb_build_object('code','OK','result',jsonb_build_object('accepted',accepted,'duplicates',duplicates,'conflicts',conflicts,'acknowledgedSequence',ack,'missingRanges',missing));
  ELSE
   IF rs.terminal_status='Completed' THEN RETURN jsonb_build_object('code','PLAYTEST_TERMINAL','status',409,'terminalStatus',rs.terminal_status);END IF;
   IF jsonb_typeof(p_input->'lastEventSequence') IS DISTINCT FROM 'number' OR (p_input->>'lastEventSequence') !~ '^[0-9]{1,18}$' OR (p_input ? 'summary' AND jsonb_typeof(p_input->'summary') IS DISTINCT FROM 'object') THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',422);END IF;
   SELECT count(*) INTO n FROM playtest_events WHERE playtest_id=session.id AND sequence<=(p_input->>'lastEventSequence')::bigint;
   IF n<>(p_input->>'lastEventSequence')::bigint THEN
    SELECT COALESCE(max(sequence),0) INTO ack FROM (SELECT sequence,row_number() OVER(ORDER BY sequence) rn FROM playtest_events WHERE playtest_id=session.id) x WHERE sequence=rn;
    RETURN jsonb_build_object('code','PLAYTEST_EVENTS_INCOMPLETE','status',409,'acknowledgedSequence',ack);
   END IF;
   result:=jsonb_build_object('lastEventSequence',(p_input->>'lastEventSequence')::bigint,'summary',COALESCE(p_input->'summary','{}'),'completedAt',stamp,'completionHash',h);
   UPDATE playtest_sessions SET status='Completed',ended_at=stamp,completion_idempotency_key='complete:'||p_actor||':'||p_key WHERE id=session.id;
   UPDATE playtest_runtime_state SET terminal_status='Completed',terminal_at=stamp,completion_hash=h,completion=result WHERE playtest_id=session.id;
   result:=jsonb_build_object('code','OK','result',playtest_state(session.id,p_family));
  END IF;
 ELSIF p_action='Cancel' THEN
  IF rs.terminal_status IS NOT NULL THEN RETURN jsonb_build_object('code','PLAYTEST_TERMINAL','status',409,'terminalStatus',rs.terminal_status);END IF;
  UPDATE playtest_sessions SET status='Cancelled',ended_at=stamp WHERE id=session.id;
  -- No Trial refund. An issued grant cannot launch anymore because /launched requires Launching.
  UPDATE playtest_runtime_state SET terminal_status='Cancelled',terminal_at=stamp WHERE playtest_id=session.id;
  UPDATE playtest_handoffs SET revoked_at=stamp,code_ciphertext=NULL WHERE playtest_id=session.id AND revoked_at IS NULL AND redeemed_at IS NULL;
  result:=jsonb_build_object('code','OK','result',playtest_state(session.id,p_family));
 END IF;
 INSERT INTO playtest_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','PlaytestSession',session.id,gen_random_uuid(),jsonb_build_object('operation',p_action),now());
 IF p_action='Handoff' THEN RETURN result||jsonb_build_object('codeCiphertext',p_input->>'codeCiphertext');END IF;
 RETURN result;
END $$;

DO $grants$ DECLARE r text;s record;sig text;BEGIN
 FOREACH sig IN ARRAY ARRAY['capability_payload_valid(jsonb,jsonb)','training_event_rejection(text,text,jsonb)',
  'learner_session_gate(text,uuid,uuid,uuid,uuid,jsonb,text)','playtest_runtime_gate(text,uuid,uuid,uuid,jsonb,text)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 -- Only the two public gates are callable by the API roles; the helpers run inside them.
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION learner_session_gate(text,uuid,uuid,uuid,uuid,jsonb,text),playtest_runtime_gate(text,uuid,uuid,uuid,jsonb,text) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM training_event_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
