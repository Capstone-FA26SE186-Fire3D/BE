-- Playtest status, Mobile handoff, grant generations, liveness, telemetry and terminal states.
-- Additive: legacy started sessions are backfilled as launched generation 1; Trial usage is never refunded or recounted.
CREATE TABLE playtest_runtime_state(
 playtest_id uuid PRIMARY KEY REFERENCES playtest_sessions(id),
 bound_family uuid,bound_at timestamptz,
 grant_generation integer NOT NULL DEFAULT 0 CHECK(grant_generation>=0),
 grant_issued_at timestamptz,grant_expires_at timestamptz,
 launched_generation integer,launched_at timestamptz,last_heartbeat_at timestamptz,
 terminal_at timestamptz,terminal_status text CHECK(terminal_status IN('Completed','Cancelled')),
 completion_hash varchar(64),completion jsonb,
 CHECK((grant_generation=0)=(grant_issued_at IS NULL)),
 CHECK(grant_expires_at IS NULL OR grant_expires_at=grant_issued_at+interval '5 minutes'),
 CHECK((terminal_status IS NULL)=(terminal_at IS NULL))
);
CREATE TABLE playtest_handoffs(
 id uuid PRIMARY KEY,playtest_id uuid NOT NULL REFERENCES playtest_sessions(id),
 code_hash varchar(64) NOT NULL UNIQUE CHECK(code_hash ~ '^[0-9a-f]{64}$'),
 -- AES-GCM ciphertext of the code, only to replay a lost create response; key stays in the API configuration.
 code_ciphertext bytea,issuing_family uuid NOT NULL,created_by uuid NOT NULL REFERENCES users(id),
 created_at timestamptz NOT NULL,expires_at timestamptz NOT NULL CHECK(expires_at=created_at+interval '5 minutes'),
 redeemed_at timestamptz,redeemed_family uuid,revoked_at timestamptz,
 CHECK((redeemed_at IS NULL)=(redeemed_family IS NULL))
);
CREATE INDEX playtest_handoffs_open ON playtest_handoffs(playtest_id) WHERE redeemed_at IS NULL AND revoked_at IS NULL;
CREATE TABLE playtest_events(
 playtest_id uuid NOT NULL REFERENCES playtest_sessions(id),event_id uuid NOT NULL,sequence bigint NOT NULL CHECK(sequence>=1),
 schema_version varchar(32) NOT NULL,event_type varchar(100) NOT NULL,occurred_at timestamptz,payload jsonb NOT NULL,
 payload_hash varchar(64) NOT NULL CHECK(payload_hash ~ '^[0-9a-f]{64}$'),received_at timestamptz NOT NULL,
 PRIMARY KEY(playtest_id,event_id),UNIQUE(playtest_id,sequence)
);
ALTER TABLE playtest_runtime_state ENABLE ROW LEVEL SECURITY;ALTER TABLE playtest_handoffs ENABLE ROW LEVEL SECURITY;ALTER TABLE playtest_events ENABLE ROW LEVEL SECURITY;
GRANT SELECT,INSERT,UPDATE ON playtest_runtime_state,playtest_handoffs TO fet3d_ifc_upload_owner;
GRANT SELECT,INSERT ON playtest_events TO fet3d_ifc_upload_owner;
DO $$ DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['playtest_runtime_state','playtest_handoffs','playtest_events'] LOOP
  EXECUTE format('CREATE POLICY playtest_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
END $$;
CREATE FUNCTION immutable_playtest_event() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN RAISE EXCEPTION 'Playtest telemetry is append-only';END $$;
CREATE TRIGGER immutable_playtest_event BEFORE UPDATE OR DELETE ON playtest_events FOR EACH ROW EXECUTE FUNCTION immutable_playtest_event();
CREATE FUNCTION immutable_playtest_terminal() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN IF OLD.terminal_status IS NOT NULL AND to_jsonb(NEW) IS DISTINCT FROM to_jsonb(OLD) THEN RAISE EXCEPTION 'Terminal playtest state is immutable';END IF;RETURN NEW;END $$;
CREATE TRIGGER immutable_playtest_terminal BEFORE UPDATE ON playtest_runtime_state FOR EACH ROW EXECUTE FUNCTION immutable_playtest_terminal();

-- Backfill: preserve stored status; sessions started by the previous gate were already reported Running.
INSERT INTO playtest_runtime_state(playtest_id,grant_generation,grant_issued_at,grant_expires_at,launched_generation,launched_at)
 SELECT s.id,CASE WHEN p.grant_issued_at IS NULL THEN 0 ELSE 1 END,p.grant_issued_at,p.grant_expires_at,
  CASE WHEN s.status='Running' AND p.grant_issued_at IS NOT NULL THEN 1 END,CASE WHEN s.status='Running' AND p.grant_issued_at IS NOT NULL THEN s.started_at END
 FROM playtest_sessions s LEFT JOIN playtest_package_pins p ON p.playtest_id=s.id;

CREATE TEMP TABLE playtest_runtime_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO playtest_runtime_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE FUNCTION fet3d_live_family(p_user uuid,p_family uuid) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT p_family IS NOT NULL AND EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_user AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp())
$$;

-- Prepare/Start keep their contract; Start now honours a Mobile binding, enters Launching and revokes open handoffs.
CREATE OR REPLACE FUNCTION playtest_lifecycle_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_building uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;s scenarios;d scenario_drafts;v scenario_versions;a revision_artifacts;m revision_artifacts;vr validation_runs;
 session playtest_sessions;pin playtest_package_pins;rs playtest_runtime_state;receipt playtest_command_receipts;ent service_entitlements;h text;result jsonb;new_id uuid;stamp timestamptz;package jsonb;
BEGIN
 IF p_action NOT IN('Prepare','Start') THEN RETURN jsonb_build_object('code','PLAYTEST_ACTION_INVALID','status',400);END IF;
 IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='Prepare' THEN SELECT * INTO s FROM scenarios WHERE id=p_resource;SELECT * INTO b FROM buildings WHERE id=s.building_id;
 ELSE SELECT * INTO session FROM playtest_sessions WHERE id=p_resource AND created_by=p_actor;SELECT * INTO b FROM buildings WHERE id=session.building_id;END IF;
 IF b.id IS NULL OR b.organization_id IS DISTINCT FROM actor.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL OR (p_building IS NOT NULL AND p_building<>b.id) OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 -- Time-based session checks must use current time after all potentially blocking locks.
 IF NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'family',p_family,'input',p_input));
 SELECT * INTO receipt FROM playtest_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
 IF receipt.result IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
  IF p_action='Start' AND (receipt.result->>'expiresAt')::timestamptz<=clock_timestamp() THEN RETURN jsonb_build_object('code','PLAYTEST_GRANT_EXPIRED','status',409);END IF;
  RETURN receipt.result;
 END IF;
 IF p_action='Prepare' THEN
  IF ((p_input->>'scenarioDraftId') IS NULL)=((p_input->>'scenarioVersionId') IS NULL) THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('scenarioVersionId',jsonb_build_array('Select exactly one draft or version.')));END IF;
  IF p_input->>'scenarioDraftId' IS NOT NULL THEN
   SELECT * INTO d FROM scenario_drafts WHERE id=(p_input->>'scenarioDraftId')::uuid AND scenario_id=s.id FOR SHARE;
   IF d.id IS NULL OR d.revision_id<>(p_input->>'revisionId')::uuid THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
   SELECT * INTO v FROM scenario_versions WHERE scenario_id=s.id AND revision_id=d.revision_id AND state_snapshot=d.state AND scenario_hash=fet3d_jsonb_payload_hash(d.state) ORDER BY version_number DESC LIMIT 1;
   IF v.id IS NULL THEN RETURN jsonb_build_object('code','DRAFT_SNAPSHOT_REQUIRED','status',409);END IF;
  ELSE SELECT * INTO v FROM scenario_versions WHERE id=(p_input->>'scenarioVersionId')::uuid AND scenario_id=s.id AND revision_id=(p_input->>'revisionId')::uuid;END IF;
  IF v.id IS NULL OR v.state_snapshot IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
  SELECT art.* INTO a FROM revision_artifacts art JOIN validation_runs run ON run.candidate_artifact_id=art.id
   JOIN processing_jobs j ON j.id=art.job_id AND j.current_attempt_id=art.attempt_id JOIN processing_job_attempts pa ON pa.id=art.attempt_id
   WHERE j.scenario_version_id=v.id AND j.revision_id=v.revision_id AND j.kind='PlaytestPackage' AND j.status='Succeeded' AND pa.status='Succeeded'
   AND art.artifact_type='unity_package' AND art.is_runtime_ready AND run.processing_attempt_id=pa.id AND run.scenario_version_id=v.id AND run.scenario_hash=v.scenario_hash AND run.outcome='Passed'
   AND NOT EXISTS(SELECT 1 FROM revision_issues WHERE validation_run_id=run.id AND severity IN('Error','Critical')) ORDER BY art.created_at DESC,art.id LIMIT 1;
  SELECT * INTO vr FROM validation_runs WHERE candidate_artifact_id=a.id AND processing_attempt_id=a.attempt_id AND scenario_version_id=v.id ORDER BY created_at DESC,id LIMIT 1;
  IF a.id IS NULL THEN RETURN jsonb_build_object('code','PLAYTEST_PACKAGE_REQUIRED','status',409);END IF;
  SELECT * INTO m FROM revision_artifacts WHERE job_id=a.job_id AND attempt_id=a.attempt_id AND artifact_type='manifest' AND is_runtime_ready;
  IF m.id IS NULL THEN RETURN jsonb_build_object('code','PLAYTEST_PACKAGE_REQUIRED','status',409);END IF;
  IF (jsonb_typeof(a.metadata->'protocolVersion')='string' AND length(a.metadata->>'protocolVersion') BETWEEN 1 AND 50
   AND jsonb_typeof(a.metadata->'manifestSchemaVersion')='string' AND length(a.metadata->>'manifestSchemaVersion') BETWEEN 1 AND 50
   AND jsonb_typeof(a.metadata->'buildTarget')='string' AND length(a.metadata->>'buildTarget') BETWEEN 1 AND 100
   AND jsonb_typeof(a.metadata->'minRuntimeVersion')='string' AND length(a.metadata->>'minRuntimeVersion')<=50
   AND a.metadata->>'minRuntimeVersion' ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
   AND jsonb_typeof(a.metadata->'requiredCapabilities')='array') IS NOT TRUE THEN RETURN jsonb_build_object('code','PACKAGE_METADATA_MISMATCH','status',409);END IF;
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(a.metadata->'requiredCapabilities') cap WHERE jsonb_typeof(cap)<>'string') THEN RETURN jsonb_build_object('code','PACKAGE_METADATA_MISMATCH','status',409);END IF;
  IF m.metadata->>'protocolVersion' IS DISTINCT FROM a.metadata->>'protocolVersion' OR m.metadata->>'manifestSchemaVersion' IS DISTINCT FROM a.metadata->>'manifestSchemaVersion' OR m.metadata->>'buildTarget' IS DISTINCT FROM a.metadata->>'buildTarget' OR m.metadata->>'minRuntimeVersion' IS DISTINCT FROM a.metadata->>'minRuntimeVersion' OR m.metadata->'requiredCapabilities' IS DISTINCT FROM a.metadata->'requiredCapabilities' THEN RETURN jsonb_build_object('code','PACKAGE_METADATA_MISMATCH','status',409);END IF;
  IF (p_input->>'packageHash' IS NOT NULL AND lower(p_input->>'packageHash')<>a.sha256_hash) OR (p_input->>'protocolVersion' IS NOT NULL AND p_input->>'protocolVersion'<>a.metadata->>'protocolVersion') OR (p_input->>'manifestSchemaVersion' IS NOT NULL AND p_input->>'manifestSchemaVersion'<>a.metadata->>'manifestSchemaVersion') THEN RETURN jsonb_build_object('code','PACKAGE_METADATA_MISMATCH','status',409);END IF;
  package:=jsonb_build_object('metadata',a.metadata,'packageHash',a.sha256_hash,'manifestHash',m.sha256_hash,'packageKey',a.object_key,'manifestKey',m.object_key,'buildTarget',a.metadata->>'buildTarget');
  new_id:=gen_random_uuid();INSERT INTO playtest_sessions(id,organization_id,building_id,revision_id,scenario_draft_id,scenario_version_id,created_by,package_hash,protocol_version,manifest_schema_version,prepare_idempotency_key,status,created_at)
   VALUES(new_id,b.organization_id,b.id,v.revision_id,d.id,v.id,p_actor,a.sha256_hash,a.metadata->>'protocolVersion',a.metadata->>'manifestSchemaVersion','prepare:'||p_actor||':'||p_key,'Created',now());
  INSERT INTO playtest_package_pins(playtest_id,artifact_id,manifest_artifact_id,validation_run_id,attempt_id,prepared_family,snapshot) VALUES(new_id,a.id,m.id,vr.id,a.attempt_id,p_family,package);
  INSERT INTO playtest_runtime_state(playtest_id) VALUES(new_id);
  result:=jsonb_build_object('code','OK','id',new_id,'scenarioVersionId',v.id,'artifactId',a.id,'validationRunId',vr.id,'packageHash',a.sha256_hash,'manifestHash',m.sha256_hash,'buildTarget',a.metadata->>'buildTarget');
 ELSE
  SELECT * INTO session FROM playtest_sessions WHERE id=p_resource AND created_by=p_actor FOR UPDATE;
  SELECT * INTO pin FROM playtest_package_pins WHERE playtest_id=session.id FOR UPDATE;
  SELECT * INTO rs FROM playtest_runtime_state WHERE playtest_id=session.id FOR UPDATE;
  -- The launching session is the Mobile family bound by handoff, otherwise the preparing family.
  IF pin.playtest_id IS NULL OR rs.playtest_id IS NULL OR COALESCE(rs.bound_family,pin.prepared_family)<>p_family THEN RETURN jsonb_build_object('code','PLAYTEST_SESSION_MISMATCH','status',409);END IF;
  IF session.status<>'Created' THEN RETURN jsonb_build_object('code','PLAYTEST_ALREADY_STARTED','status',409);END IF;
  IF package_runtime_compatible(pin.snapshot->'metadata',p_input->>'runtimeVersion') IS NOT TRUE THEN RETURN jsonb_build_object('code','RUNTIME_INCOMPATIBLE','status',409);END IF;
  IF NOT EXISTS(SELECT 1 FROM processing_job_attempts pa JOIN processing_jobs j ON j.id=pa.processing_job_id AND j.current_attempt_id=pa.id JOIN revision_artifacts accepted_art ON accepted_art.id=pin.artifact_id AND accepted_art.attempt_id=pa.id WHERE pa.id=pin.attempt_id AND pa.status='Succeeded' AND j.status='Succeeded' AND accepted_art.is_runtime_ready) THEN RETURN jsonb_build_object('code','PLAYTEST_PACKAGE_REQUIRED','status',409);END IF;
  stamp:=clock_timestamp();
  SELECT e.* INTO ent FROM service_entitlements e WHERE e.organization_id=b.organization_id AND e.building_id=b.id AND e.starts_at<=stamp AND e.ends_at>stamp AND
   ((e.status='Active' AND EXISTS(SELECT 1 FROM payment_transactions tx JOIN payos_payment_requests pr ON pr.id=tx.payment_request_id JOIN quotations q ON q.id=pr.quotation_id WHERE tx.id=e.payment_transaction_id AND tx.status='Applied' AND q.billing_purpose='BuildingService' AND q.organization_id=b.organization_id)) OR (e.status='Trial' AND e.playtest_units_used<e.playtest_units_granted))
   ORDER BY CASE WHEN e.status='Active' THEN 0 ELSE 1 END,e.ends_at DESC,e.id LIMIT 1 FOR UPDATE;
  IF NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
  IF ent.id IS NULL OR ent.ends_at<=clock_timestamp() THEN RETURN jsonb_build_object('code','PLAYTEST_ENTITLEMENT_REQUIRED','status',409);END IF;
  stamp:=clock_timestamp(); -- Issue the full five-minute grant only after entitlement locking completes.
  IF ent.status='Trial' THEN UPDATE service_entitlements SET playtest_units_used=playtest_units_used+1 WHERE id=ent.id;END IF;
  UPDATE playtest_sessions SET service_entitlement_id=ent.id,status='Launching',started_at=stamp,runtime_version=p_input->>'runtimeVersion',start_idempotency_key='start:'||p_actor||':'||p_key WHERE id=session.id;
  UPDATE playtest_package_pins SET started_family=p_family,grant_issued_at=stamp,grant_expires_at=stamp+interval '5 minutes' WHERE playtest_id=session.id;
  UPDATE playtest_runtime_state SET grant_generation=1,grant_issued_at=stamp,grant_expires_at=stamp+interval '5 minutes' WHERE playtest_id=session.id;
  UPDATE playtest_handoffs SET revoked_at=stamp,code_ciphertext=NULL WHERE playtest_id=session.id AND revoked_at IS NULL AND redeemed_at IS NULL;
  result:=jsonb_build_object('code','OK','playtestId',session.id,'actorId',p_actor,'familyId',p_family,'scenarioVersionId',session.scenario_version_id,'artifactId',pin.artifact_id,'validationRunId',pin.validation_run_id,'packageHash',pin.snapshot->>'packageHash','manifestHash',pin.snapshot->>'manifestHash','buildTarget',pin.snapshot->>'buildTarget','packageKey',pin.snapshot->>'packageKey','manifestKey',pin.snapshot->>'manifestKey','protocolVersion',session.protocol_version,'manifestSchemaVersion',session.manifest_schema_version,'runtimeVersion',p_input->>'runtimeVersion','issuedAt',stamp,'expiresAt',stamp+interval '5 minutes','generation',1);
 END IF;
 INSERT INTO playtest_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create','PlaytestSession',CASE WHEN p_action='Prepare' THEN new_id ELSE session.id END,gen_random_uuid(),jsonb_build_object('operation',p_action,'scenarioVersionId',CASE WHEN p_action='Prepare' THEN v.id ELSE session.scenario_version_id END),now());
 RETURN result;
END $$;

CREATE FUNCTION playtest_state(p_id uuid,p_family uuid) RETURNS jsonb LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('id',s.id,'buildingId',s.building_id,'revisionId',s.revision_id,'scenarioVersionId',s.scenario_version_id,
  'status',s.status,'createdAt',s.created_at,'startedAt',s.started_at,'endedAt',s.ended_at,'runtimeVersion',s.runtime_version,
  'package',jsonb_build_object('artifactId',p.artifact_id,'manifestArtifactId',p.manifest_artifact_id,'validationRunId',p.validation_run_id,
    'packageHash',p.snapshot->>'packageHash','manifestHash',p.snapshot->>'manifestHash','buildTarget',p.snapshot->>'buildTarget',
    'protocolVersion',s.protocol_version,'manifestSchemaVersion',s.manifest_schema_version),
  'grant',CASE WHEN r.grant_generation>0 THEN jsonb_build_object('generation',r.grant_generation,'issuedAt',r.grant_issued_at,'expiresAt',r.grant_expires_at) END,
  'launchedAt',r.launched_at,'lastHeartbeatAt',r.last_heartbeat_at,
  'acknowledgedSequence',COALESCE((SELECT max(sequence) FROM (SELECT sequence,row_number() OVER(ORDER BY sequence) n FROM playtest_events WHERE playtest_id=s.id) x WHERE sequence=n),0),
  'boundToMobile',r.bound_family IS NOT NULL,
  'launchSession',CASE WHEN s.status='Created' THEN COALESCE(r.bound_family,p.prepared_family)=p_family ELSE p.started_family=p_family END,
  'openHandoffExpiresAt',(SELECT max(h.expires_at) FROM playtest_handoffs h WHERE h.playtest_id=s.id AND h.redeemed_at IS NULL AND h.revoked_at IS NULL AND h.expires_at>clock_timestamp()),
  'recovery',jsonb_build_object(
    'canStart',s.status='Created' AND COALESCE(r.bound_family,p.prepared_family)=p_family,
    'canReissueGrant',s.status='Launching' AND p.started_family=p_family,
    'canSync',s.status='Running' AND p.started_family=p_family,
    'canCancel',s.status IN('Created','Launching','Running')),
  'completion',r.completion)
 FROM playtest_sessions s JOIN playtest_package_pins p ON p.playtest_id=s.id JOIN playtest_runtime_state r ON r.playtest_id=s.id WHERE s.id=p_id
$$;

CREATE FUNCTION playtest_runtime_gate(p_action text,p_actor uuid,p_family uuid,p_playtest uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;session playtest_sessions;pin playtest_package_pins;rs playtest_runtime_state;ho playtest_handoffs;receipt playtest_command_receipts;
 ent service_entitlements;h text;result jsonb;stamp timestamptz;e jsonb;n bigint;accepted jsonb:='[]';duplicates jsonb:='[]';conflicts jsonb:='[]';missing jsonb;ack bigint;existing playtest_events;
BEGIN
 IF p_action NOT IN('Get','Handoff','Redeem','Reissue','Launched','Heartbeat','Events','Complete','Cancel') THEN RETURN jsonb_build_object('code','PLAYTEST_ACTION_INVALID','status',400);END IF;
 IF p_action IN('Handoff','Reissue','Complete','Cancel') AND (p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]') THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='Redeem' THEN
  IF p_input->>'codeHash' IS NULL OR p_input->>'codeHash' !~ '^[0-9a-f]{64}$' THEN RETURN jsonb_build_object('code','PLAYTEST_HANDOFF_INVALID','status',404);END IF;
  SELECT * INTO ho FROM playtest_handoffs WHERE code_hash=p_input->>'codeHash' FOR UPDATE;
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
 IF NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 stamp:=clock_timestamp();
 IF p_key IS NOT NULL AND p_action IN('Handoff','Reissue','Complete','Cancel') THEN
  -- Receipts bind actor/operation/resource/canonical input; the session family is checked live, not hashed.
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',session.id,'input',p_input-'codeHash'-'codeCiphertext'));
  SELECT * INTO receipt FROM playtest_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   IF p_action='Handoff' THEN
    SELECT * INTO ho FROM playtest_handoffs WHERE id=(receipt.result->>'handoffId')::uuid;
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
     IF existing.payload_hash=h THEN duplicates:=duplicates||to_jsonb(e->>'eventId');ELSE conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_HASH_CONFLICT');END IF;
    ELSIF EXISTS(SELECT 1 FROM playtest_events WHERE playtest_id=session.id AND sequence=(e->>'sequence')::bigint) THEN
     conflicts:=conflicts||jsonb_build_object('eventId',e->>'eventId','code','EVENT_SEQUENCE_CONFLICT');
    ELSE
     INSERT INTO playtest_events(playtest_id,event_id,sequence,schema_version,event_type,occurred_at,payload,payload_hash,received_at)
      VALUES(session.id,(e->>'eventId')::uuid,(e->>'sequence')::bigint,e->>'schemaVersion',e->>'type',(e->>'occurredAt')::timestamptz,e->'payload',h,stamp);
     accepted:=accepted||to_jsonb(e->>'eventId');
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

DO $$ DECLARE sig text;r text;s record;BEGIN
 FOREACH sig IN ARRAY ARRAY['fet3d_live_family(uuid,uuid)','playtest_state(uuid,uuid)','playtest_runtime_gate(text,uuid,uuid,uuid,jsonb,text)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION playtest_runtime_gate(text,uuid,uuid,uuid,jsonb,text) TO %I',r);
  EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON playtest_runtime_state,playtest_handoffs,playtest_events FROM %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM playtest_runtime_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $$;

-- Pending-registration cleanup fails closed on unreadable references: expose the new user reference to its owner.
DO $cleanup$ BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  GRANT SELECT ON playtest_handoffs TO fet3d_pending_cleanup_owner;
  CREATE POLICY pending_cleanup_owner ON playtest_handoffs TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true);
 END IF;
END $cleanup$;
