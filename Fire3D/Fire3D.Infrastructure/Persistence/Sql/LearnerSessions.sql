-- Learner lifecycle on the existing sessions/session_events/session_results tables (contract_version 7).
-- Legacy rows keep their stored values and contract_version 1; nothing is relabelled as v7.
CREATE TEMP TABLE learner_session_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO learner_session_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- 1. Building QR: identifies a Building only; never a release, a grant or an entitlement.
CREATE TABLE building_qr_codes(
 id uuid PRIMARY KEY,building_id uuid NOT NULL REFERENCES buildings(id),organization_id uuid NOT NULL REFERENCES organizations(id),
 token_hash varchar(64) NOT NULL UNIQUE CHECK(token_hash ~ '^[0-9a-f]{64}$'),label varchar(255),
 status text NOT NULL CHECK(status IN('Active','Revoked')),created_by uuid NOT NULL REFERENCES users(id),
 created_at timestamptz NOT NULL,revoked_at timestamptz,replaced_by uuid REFERENCES building_qr_codes(id),
 CHECK((status='Revoked')=(revoked_at IS NOT NULL)));
CREATE INDEX building_qr_codes_building ON building_qr_codes(building_id,created_at DESC);

-- 2. Preparation pins Training/release/version/rubric/package/runtime; no grant, no seat, not a play.
CREATE TABLE training_session_preparations(
 id uuid PRIMARY KEY,trainee_user_id uuid NOT NULL REFERENCES users(id),organization_id uuid NOT NULL REFERENCES organizations(id),
 building_id uuid NOT NULL REFERENCES buildings(id),training_id uuid NOT NULL,release_id uuid NOT NULL,scenario_version_id uuid NOT NULL,
 mode text NOT NULL CHECK(mode IN('Learn','Guided','Assessment')),scenario_hash varchar(64) NOT NULL,rubric_hash varchar(64),
 package jsonb NOT NULL,runtime_version text NOT NULL,status text NOT NULL CHECK(status IN('Prepared','Started')),
 created_at timestamptz NOT NULL,expires_at timestamptz NOT NULL,started_at timestamptz,
 FOREIGN KEY(training_id,release_id,scenario_version_id,organization_id) REFERENCES trainings(id,release_id,scenario_version_id,organization_id));
CREATE TABLE learner_command_receipts(
 actor_id uuid NOT NULL REFERENCES users(id),operation text NOT NULL,idempotency_key varchar(128) NOT NULL,
 input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[0-9a-f]{64}$'),result jsonb NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(actor_id,operation,idempotency_key));

-- 3. Started learner sessions: same table as legacy, contract_version 7 with server pins.
ALTER TABLE sessions ALTER COLUMN device_id DROP NOT NULL,ALTER COLUMN qr_code_id DROP NOT NULL,
 ALTER COLUMN app_version DROP NOT NULL,ALTER COLUMN unity_version DROP NOT NULL;
ALTER TABLE sessions
 ADD COLUMN contract_version integer NOT NULL DEFAULT 1,ADD COLUMN building_id uuid REFERENCES buildings(id),
 ADD COLUMN entitlement_id uuid REFERENCES service_entitlements(id),ADD COLUMN seat_id uuid REFERENCES billing_learner_seats(id),
 ADD COLUMN rubric_hash varchar(64),ADD COLUMN package jsonb,ADD COLUMN runtime_version text,
 ADD COLUMN launch_family uuid,ADD COLUMN grant_generation integer,ADD COLUMN grant_issued_at timestamptz,ADD COLUMN grant_expires_at timestamptz,
 ADD COLUMN last_heartbeat_at timestamptz,ADD COLUMN continuation_id uuid,ADD COLUMN continuation_expires_at timestamptz,
 ADD COLUMN completion_key varchar(128),ADD COLUMN completion_hash varchar(64),ADD COLUMN completion_last_sequence bigint,
 ADD COLUMN completion_end_reason text,ADD COLUMN completion_requested_at timestamptz;
ALTER TABLE sessions ADD CONSTRAINT sessions_contract_shape CHECK(
 (contract_version=1 AND device_id IS NOT NULL AND qr_code_id IS NOT NULL AND app_version IS NOT NULL AND unity_version IS NOT NULL)
 OR (contract_version=7 AND building_id IS NOT NULL AND entitlement_id IS NOT NULL AND seat_id IS NOT NULL
  AND package IS NOT NULL AND runtime_version IS NOT NULL
  AND launch_family IS NOT NULL AND grant_generation>=1 AND grant_expires_at=grant_issued_at+interval '5 minutes'
  AND (completion_key IS NULL)=(completion_requested_at IS NULL) AND (completion_end_reason IS NULL OR completion_end_reason IN('Finished','Abandoned','TimedOut'))));
CREATE INDEX sessions_v7_building_started ON sessions(building_id,started_at) WHERE contract_version=7;

ALTER TABLE session_events ADD COLUMN payload_hash varchar(64) CHECK(payload_hash ~ '^[0-9a-f]{64}$');
CREATE FUNCTION immutable_session_event() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN RAISE EXCEPTION 'Session telemetry is append-only';END $$;
CREATE TRIGGER immutable_session_event BEFORE UPDATE OR DELETE ON session_events FOR EACH ROW EXECUTE FUNCTION immutable_session_event();

ALTER TABLE session_results ALTER COLUMN score DROP NOT NULL,ALTER COLUMN time_taken_seconds DROP NOT NULL,ALTER COLUMN wrong_exits DROP NOT NULL,
 ALTER COLUMN hazard_exposure_score DROP NOT NULL,ALTER COLUMN total_distance_meters DROP NOT NULL,ALTER COLUMN reached_exit DROP NOT NULL,
 ALTER COLUMN path_traveled DROP NOT NULL;
ALTER TABLE session_results ADD COLUMN contract_version integer NOT NULL DEFAULT 1,ADD COLUMN mode text,
 ADD COLUMN outcome text CHECK(outcome IN('Passed','NotPassed','Incomplete','NotAssessed')),ADD COLUMN outcome_reason text,
 ADD COLUMN rubric_hash varchar(64),ADD COLUMN criterion_results jsonb,ADD COLUMN metrics jsonb;
ALTER TABLE session_results ADD CONSTRAINT session_results_contract_shape CHECK(contract_version=1 OR (contract_version=7 AND outcome IS NOT NULL AND metrics IS NOT NULL AND mode IS NOT NULL));
CREATE FUNCTION immutable_session_result() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN RAISE EXCEPTION 'Session results are immutable';END $$;
CREATE TRIGGER immutable_session_result BEFORE UPDATE OR DELETE ON session_results FOR EACH ROW EXECUTE FUNCTION immutable_session_result();

-- 4. Read model shared by every action.
CREATE FUNCTION learner_session_state(p_id uuid,p_family uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT COALESCE(
  (SELECT jsonb_build_object('id',s.id,'status',CASE WHEN s.status='Running' AND s.completion_requested_at IS NOT NULL THEN 'AwaitingSync' ELSE s.status::text END,'trainingId',s.training_id,'releaseId',s.release_id,'scenarioVersionId',s.scenario_version_id,
    'buildingId',s.building_id,'mode',s.mode,'runtimeVersion',s.runtime_version,'scenarioHash',s.scenario_hash,'rubricHash',s.rubric_hash,
    'package',s.package-'packageKey'-'manifestKey','startedAt',s.started_at,'launchedAt',s.launched_at,'endedAt',s.ended_at,'lastHeartbeatAt',s.last_heartbeat_at,
    'grant',jsonb_build_object('generation',s.grant_generation,'issuedAt',s.grant_issued_at,'expiresAt',s.grant_expires_at),
    'launchSession',s.launch_family=p_family,'continuationExpiresAt',s.continuation_expires_at,
    'acknowledgedSequence',COALESCE((SELECT max(e.sequence_number) FROM (SELECT sequence_number,row_number() OVER(ORDER BY sequence_number) rn FROM session_events WHERE session_id=s.id) e WHERE e.sequence_number=e.rn),0),
    'completion',CASE WHEN s.completion_key IS NULL THEN NULL ELSE jsonb_build_object('lastEventSequence',s.completion_last_sequence,'endReason',s.completion_end_reason,'requestedAt',s.completion_requested_at) END,
    'hasResult',EXISTS(SELECT 1 FROM session_results r WHERE r.session_id=s.id))
   FROM sessions s WHERE s.id=p_id AND s.contract_version=7),
  (SELECT jsonb_build_object('id',p.id,'status',CASE WHEN p.expires_at<=clock_timestamp() THEN 'Expired' ELSE 'Prepared' END,'trainingId',p.training_id,'releaseId',p.release_id,
    'scenarioVersionId',p.scenario_version_id,'buildingId',p.building_id,'mode',p.mode,'runtimeVersion',p.runtime_version,'scenarioHash',p.scenario_hash,
    'rubricHash',p.rubric_hash,'package',p.package-'packageKey'-'manifestKey','preparedAt',p.created_at,'expiresAt',p.expires_at,'hasResult',false)
   FROM training_session_preparations p WHERE p.id=p_id AND p.status='Prepared'))
$$;

CREATE FUNCTION learner_session_ack(p_id uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object(
  'acknowledgedSequence',COALESCE((SELECT max(sequence_number) FROM (SELECT sequence_number,row_number() OVER(ORDER BY sequence_number) rn FROM session_events WHERE session_id=p_id) x WHERE sequence_number=rn),0),
  'missingRanges',COALESCE((SELECT jsonb_agg(jsonb_build_object('from',gap_start,'to',gap_end) ORDER BY gap_start) FROM (
    SELECT COALESCE(lag(sequence_number) OVER(ORDER BY sequence_number),0)+1 gap_start,sequence_number-1 gap_end FROM session_events WHERE session_id=p_id) g WHERE gap_end>=gap_start),'[]'))
$$;

-- Server-side rubric evaluation from received telemetry only. Completion is never treated as Passed.
CREATE FUNCTION learner_session_finalize(p_id uuid) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE s sessions;v scenario_versions;metrics jsonb;c jsonb;results jsonb:='[]';val jsonb;passed boolean;total_w numeric:=0;passed_w numeric:=0;
 mandatory_failed boolean:=false;score numeric;outcome text;reason text;rid uuid;threshold numeric;
BEGIN
 SELECT * INTO s FROM sessions WHERE id=p_id FOR UPDATE;
 IF EXISTS(SELECT 1 FROM session_results WHERE session_id=p_id) THEN RETURN (SELECT id FROM session_results WHERE session_id=p_id); END IF;
 SELECT * INTO v FROM scenario_versions WHERE id=s.scenario_version_id;
 SELECT jsonb_build_object(
   'reached_exit',CASE WHEN bool_or(event_type='ExitReached') THEN 1 ELSE 0 END,
   'completion_time_seconds',round(COALESCE(min(elapsed_ms) FILTER(WHERE event_type='ExitReached'),max(elapsed_ms))/1000.0,3),
   'wrong_exits',count(*) FILTER(WHERE event_type='WrongExit'),
   'hazard_exposure',COALESCE(sum((event_data->>'amount')::numeric) FILTER(WHERE event_type='HazardExposure'),0),
   'distance_meters',COALESCE(sum((event_data->>'distanceMeters')::numeric) FILTER(WHERE event_type='Moved'),0))
  INTO metrics FROM session_events WHERE session_id=p_id AND sequence_number<=s.completion_last_sequence;
 metrics:=jsonb_strip_nulls(metrics);
 IF jsonb_typeof(v.rubric->'criteria')='array' THEN
  FOR c IN SELECT value FROM jsonb_array_elements(v.rubric->'criteria') LOOP
   val:=metrics->(c->>'metric');threshold:=(c->>'threshold')::numeric;
   -- A missing metric is a failed criterion, never unknown.
   passed:=COALESCE(jsonb_typeof(val)='number' AND CASE c->>'operator' WHEN 'gte' THEN val::text::numeric>=threshold WHEN 'lte' THEN val::text::numeric<=threshold WHEN 'eq' THEN val::text::numeric=threshold ELSE false END,false);
   results:=results||jsonb_build_array(jsonb_build_object('id',c->>'id','metric',c->>'metric','value',val,'operator',c->>'operator','threshold',threshold,
    'mandatory',(c->>'mandatory')::boolean,'weight',(c->>'weight')::numeric,'passed',passed,'reason',CASE WHEN val IS NULL THEN 'METRIC_UNAVAILABLE' END));
   total_w:=total_w+(c->>'weight')::numeric;
   IF passed THEN passed_w:=passed_w+(c->>'weight')::numeric;ELSIF (c->>'mandatory')::boolean THEN mandatory_failed:=true;END IF;
  END LOOP;
 END IF;
 score:=CASE WHEN total_w>0 THEN round(100*passed_w/total_w,2) END;
 IF s.mode<>'Assessment' THEN outcome:='NotAssessed';reason:='MODE_NOT_ASSESSED';score:=NULL;
 ELSIF s.completion_end_reason='Abandoned' THEN outcome:='Incomplete';reason:='ENDED_BEFORE_COMPLETION';score:=NULL;
 ELSIF total_w=0 OR v.rubric IS NULL THEN outcome:='NotAssessed';reason:='RUBRIC_NOT_SCORABLE';score:=NULL;
 ELSIF mandatory_failed THEN outcome:='NotPassed';reason:='MANDATORY_CRITERION_FAILED';
 ELSIF passed_w/total_w>=(v.rubric->>'pass_threshold')::numeric THEN outcome:='Passed';reason:='ALL_REQUIREMENTS_MET';
 ELSE outcome:='NotPassed';reason:='BELOW_PASS_THRESHOLD';END IF;
 rid:=gen_random_uuid();
 INSERT INTO session_results(id,session_id,completion_key,last_event_sequence,submission_payload,result_schema_version,rubric_version,score,time_taken_seconds,wrong_exits,
  hazard_exposure_score,total_distance_meters,reached_exit,path_traveled,synced_at,created_at,contract_version,mode,outcome,outcome_reason,rubric_hash,criterion_results,metrics)
 VALUES(rid,s.id,gen_random_uuid(),s.completion_last_sequence,jsonb_build_object('lastEventSequence',s.completion_last_sequence,'endReason',s.completion_end_reason),'7',
  COALESCE(v.rubric->>'schema_version','none'),score,round((metrics->>'completion_time_seconds')::numeric)::int,(metrics->>'wrong_exits')::int,
  round((metrics->>'hazard_exposure')::numeric,2),round((metrics->>'distance_meters')::numeric,2),(metrics->>'reached_exit')::int=1,NULL,clock_timestamp(),clock_timestamp(),
  7,s.mode::text,outcome,reason,s.rubric_hash,CASE WHEN s.mode='Assessment' THEN results END,metrics);
 UPDATE sessions SET status='Completed',ended_at=clock_timestamp() WHERE id=s.id;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),s.trainee_user_id,s.organization_id,'System','Create','SessionResult',rid,s.id,jsonb_build_object('outcome',outcome,'mode',s.mode::text),now());
 RETURN rid;
END $$;

CREATE FUNCTION learner_session_gate(p_action text,p_actor uuid,p_family uuid,p_continuation uuid,p_resource uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;t trainings;rel releases;pk release_packages;pin release_build_provenance;cr scenario_content_reviews;v scenario_versions;
 art revision_artifacts;prep training_session_preparations;s sessions;ent service_entitlements;seat billing_learner_seats;receipt learner_command_receipts;
 h text;result jsonb;stamp timestamptz;new_id uuid;lim integer;used integer;e jsonb;existing session_events;accepted jsonb:='[]';duplicates jsonb:='[]';conflicts jsonb:='[]';
 ack jsonb;live boolean;package jsonb;ttl integer;
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
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   IF p_action='Complete' AND receipt.result->>'status'='AwaitingSync' THEN
    -- Retried completion: finalize if the missing events have arrived since.
    SELECT * INTO s FROM sessions WHERE id=p_resource AND trainee_user_id=p_actor FOR UPDATE;
    IF s.status='Running' AND s.completion_requested_at IS NOT NULL AND (learner_session_ack(s.id)->>'acknowledgedSequence')::bigint>=s.completion_last_sequence THEN PERFORM learner_session_finalize(s.id);END IF;
    RETURN jsonb_build_object('code','OK','status',CASE WHEN EXISTS(SELECT 1 FROM session_results WHERE session_id=p_resource) THEN 200 ELSE 202 END,
     'result',jsonb_build_object('status',learner_session_state(p_resource,p_family)->>'status','session',learner_session_state(p_resource,p_family))||learner_session_ack(p_resource));
   END IF;
   RETURN jsonb_build_object('code','OK','result',receipt.result);
  END IF;
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
   stamp:=clock_timestamp();
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
  IF NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
  IF NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF b.visibility<>'Public' AND NOT EXISTS(SELECT 1 FROM building_participation_grants WHERE building_id=b.id AND trainee_user_id=p_actor AND access_revision=b.access_revision) THEN RETURN jsonb_build_object('code','BUILDING_ACCESS_REQUIRED','status',403);END IF;
  IF t.status<>'Active' OR rel.status<>'Published' OR (t.start_date IS NOT NULL AND t.start_date>clock_timestamp()) OR (t.end_date IS NOT NULL AND t.end_date<=clock_timestamp()) THEN RETURN jsonb_build_object('code','TRAINING_UNAVAILABLE','status',409);END IF;
  IF NOT EXISTS(SELECT 1 FROM scenario_content_reviews WHERE id=(prep.package->>'contentReviewId')::uuid AND status='Approved') THEN RETURN jsonb_build_object('code','CONTENT_APPROVAL_REQUIRED','status',409);END IF;
  IF package_runtime_compatible((SELECT metadata FROM revision_artifacts WHERE id=(prep.package->>'artifactId')::uuid),prep.runtime_version) IS NOT TRUE THEN RETURN jsonb_build_object('code','RUNTIME_INCOMPATIBLE','status',409);END IF;
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
    IF existing.payload_hash=h THEN duplicates:=duplicates||to_jsonb(e->>'eventId');ELSE conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_HASH_CONFLICT');END IF;
   ELSIF EXISTS(SELECT 1 FROM session_events WHERE session_id=s.id AND sequence_number=(e->>'sequence')::bigint) THEN
    conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_SEQUENCE_CONFLICT');
   ELSIF s.completion_last_sequence IS NOT NULL AND (e->>'sequence')::bigint>s.completion_last_sequence THEN
    conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_AFTER_COMPLETION');
   ELSE
    INSERT INTO session_events(id,session_id,sequence_number,client_event_id,schema_version,event_type,event_data,elapsed_ms,recorded_at,received_at,payload_hash)
    VALUES(gen_random_uuid(),s.id,(e->>'sequence')::bigint,(e->>'eventId')::uuid,e->>'schemaVersion',e->>'type',e->'payload',(e->>'elapsedMs')::bigint,(e->>'occurredAt')::timestamptz,clock_timestamp(),h);
    accepted:=accepted||to_jsonb(e->>'eventId');
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

-- 5. Building QR gate: Organization/Admin manage codes; any signed-in role resolves a token to its Building.
CREATE FUNCTION building_qr_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;q building_qr_codes;n building_qr_codes;stamp timestamptz:=clock_timestamp();result jsonb;
BEGIN
 IF p_action NOT IN('Create','List','Rotate','Revoke','Resolve') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_action='Resolve' THEN
  SELECT * INTO q FROM building_qr_codes WHERE token_hash=p_input->>'tokenHash' AND status='Active';
  SELECT * INTO b FROM buildings WHERE id=q.building_id;
  IF q.id IS NULL OR b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL)
   OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','QR_NOT_FOUND','status',404);END IF;
  RETURN jsonb_build_object('code','OK','result',jsonb_build_object('buildingId',b.id,'buildingName',b.name,'visibility',b.visibility,
   'hasAccess',actor.role<>'Trainee' OR b.visibility='Public' OR EXISTS(SELECT 1 FROM building_participation_grants WHERE building_id=b.id AND trainee_user_id=p_actor AND access_revision=b.access_revision)));
 END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 SELECT * INTO b FROM buildings WHERE id=p_resource;
 IF b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 IF p_action='List' THEN
  SELECT COALESCE(jsonb_agg(jsonb_build_object('id',x.id,'label',x.label,'status',x.status,'createdAt',x.created_at,'revokedAt',x.revoked_at,'replacedBy',x.replaced_by) ORDER BY x.created_at DESC,x.id),'[]')
   INTO result FROM building_qr_codes x WHERE x.building_id=b.id;
  RETURN jsonb_build_object('code','OK','result',result);
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF p_action IN('Rotate','Revoke') THEN
  SELECT * INTO q FROM building_qr_codes WHERE id=(p_input->>'qrId')::uuid AND building_id=b.id FOR UPDATE;
  IF q.id IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  IF q.status<>'Active' THEN RETURN jsonb_build_object('code','QR_ALREADY_REVOKED','status',409);END IF;
 END IF;
 IF p_action IN('Create','Rotate') THEN
  IF p_input->>'tokenHash' !~ '^[0-9a-f]{64}$' OR p_input->>'tokenHash' IS NULL OR length(COALESCE(p_input->>'label',''))>255 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
  INSERT INTO building_qr_codes(id,building_id,organization_id,token_hash,label,status,created_by,created_at)
  VALUES(gen_random_uuid(),b.id,b.organization_id,p_input->>'tokenHash',COALESCE(NULLIF(btrim(p_input->>'label'),''),q.label),'Active',p_actor,stamp) RETURNING * INTO n;
 END IF;
 IF p_action IN('Rotate','Revoke') THEN UPDATE building_qr_codes SET status='Revoked',revoked_at=stamp,replaced_by=n.id WHERE id=q.id;END IF;
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','BuildingQrCode',COALESCE(n.id,q.id),gen_random_uuid(),jsonb_build_object('operation',p_action,'previousId',q.id),now());
 RETURN jsonb_build_object('code','OK','result',jsonb_build_object('id',COALESCE(n.id,q.id),'buildingId',b.id,'label',COALESCE(n.label,q.label),'status',CASE WHEN p_action='Revoke' THEN 'Revoked' ELSE 'Active' END,'replacedId',CASE WHEN p_action='Rotate' THEN q.id END));
END $$;

-- 6. Privileges: gates only; no runtime DML on learner tables.
DO $grants$ DECLARE t text;r text;sig text;s record;BEGIN
 FOREACH t IN ARRAY ARRAY['building_qr_codes','training_session_preparations','learner_command_receipts'] LOOP
  EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['building_qr_codes','training_session_preparations','learner_command_receipts','sessions','session_events','session_results','billing_learner_seats',
   'trainings','releases','release_packages','release_build_provenance','scenario_content_reviews','building_participation_grants','service_entitlements','revision_artifacts','scenario_versions'] LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=t AND policyname='learner_gate_owner') THEN
   EXECUTE format('CREATE POLICY learner_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
  END IF;
 END LOOP;
 GRANT SELECT,INSERT,UPDATE ON building_qr_codes,training_session_preparations,sessions TO fet3d_ifc_upload_owner;
 GRANT SELECT,INSERT ON learner_command_receipts,session_events,session_results,billing_learner_seats TO fet3d_ifc_upload_owner;
 GRANT SELECT ON trainings,releases,release_packages,release_build_provenance,scenario_content_reviews,building_participation_grants,service_entitlements,revision_artifacts,scenario_versions TO fet3d_ifc_upload_owner;
 GRANT UPDATE(id) ON service_entitlements TO fet3d_ifc_upload_owner;
 GRANT INSERT ON audit_logs TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['learner_session_state(uuid,uuid)','learner_session_ack(uuid)','learner_session_finalize(uuid)','learner_session_gate(text,uuid,uuid,uuid,uuid,jsonb,text)','building_qr_gate(text,uuid,uuid,uuid,jsonb)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION learner_session_gate(text,uuid,uuid,uuid,uuid,jsonb,text),building_qr_gate(text,uuid,uuid,uuid,jsonb) TO %I',r);
  EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON building_qr_codes,training_session_preparations,learner_command_receipts,session_events,session_results,billing_learner_seats FROM %I',r);
  EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON sessions FROM %I',r);
 END IF;END LOOP;
 FOREACH t IN ARRAY ARRAY['building_qr_codes','training_session_preparations','learner_command_receipts'] LOOP
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON %I FROM %I',t,r);END IF;END LOOP;
  EXECUTE format('REVOKE ALL ON %I FROM PUBLIC',t);
 END LOOP;
 -- Pending-registration cleanup fails closed on unreadable user references.
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  FOREACH t IN ARRAY ARRAY['building_qr_codes','training_session_preparations','learner_command_receipts'] LOOP
   EXECUTE format('GRANT SELECT ON %I TO fet3d_pending_cleanup_owner',t);
   EXECUTE format('CREATE POLICY pending_cleanup_owner ON %I TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true)',t);
  END LOOP;
 END IF;
 SELECT * INTO s FROM learner_session_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
