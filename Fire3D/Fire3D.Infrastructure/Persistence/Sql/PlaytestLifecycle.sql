-- Preparation has no entitlement or grant. All new sessions require immutable accepted package pins.
ALTER TABLE playtest_sessions ALTER COLUMN service_entitlement_id DROP NOT NULL;
CREATE TABLE playtest_package_pins(
 playtest_id uuid PRIMARY KEY REFERENCES playtest_sessions(id),artifact_id uuid NOT NULL REFERENCES revision_artifacts(id),
 manifest_artifact_id uuid NOT NULL REFERENCES revision_artifacts(id),validation_run_id uuid NOT NULL REFERENCES validation_runs(id),
 attempt_id uuid NOT NULL REFERENCES processing_job_attempts(id),prepared_family uuid NOT NULL,snapshot jsonb NOT NULL,
 started_family uuid,grant_issued_at timestamptz,grant_expires_at timestamptz,
 CHECK((started_family IS NULL AND grant_issued_at IS NULL AND grant_expires_at IS NULL) OR (started_family IS NOT NULL AND grant_expires_at=grant_issued_at+interval '5 minutes'))
);
CREATE TABLE playtest_command_receipts(actor_id uuid NOT NULL REFERENCES users(id),operation text NOT NULL,idempotency_key varchar(128) NOT NULL,
 input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[0-9a-f]{64}$'),result jsonb NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(actor_id,operation,idempotency_key));
ALTER TABLE playtest_package_pins ENABLE ROW LEVEL SECURITY;ALTER TABLE playtest_command_receipts ENABLE ROW LEVEL SECURITY;
GRANT SELECT,INSERT,UPDATE ON playtest_sessions,playtest_package_pins TO fet3d_ifc_upload_owner;
GRANT SELECT,INSERT ON playtest_command_receipts TO fet3d_ifc_upload_owner;
GRANT SELECT ON auth_refresh_tokens,service_entitlements,payment_transactions,payos_payment_requests,quotations TO fet3d_ifc_upload_owner;
GRANT UPDATE(playtest_units_used) ON service_entitlements TO fet3d_ifc_upload_owner;
DO $$ DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['playtest_sessions','playtest_package_pins','playtest_command_receipts','auth_refresh_tokens','service_entitlements','payment_transactions','payos_payment_requests','quotations'] LOOP
  EXECUTE format('CREATE POLICY playtest_gate_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
END $$;
CREATE FUNCTION immutable_playtest_pin() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN IF TG_OP='DELETE' OR (to_jsonb(NEW)-ARRAY['started_family','grant_issued_at','grant_expires_at']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['started_family','grant_issued_at','grant_expires_at']) OR OLD.started_family IS NOT NULL THEN RAISE EXCEPTION 'Playtest package and launch identity are immutable';END IF;RETURN NEW;END $$;
CREATE TRIGGER immutable_playtest_pin BEFORE UPDATE OR DELETE ON playtest_package_pins FOR EACH ROW EXECUTE FUNCTION immutable_playtest_pin();

-- Shared runtime compatibility contract, reusable by future release/start gates.
CREATE FUNCTION package_runtime_compatible(p_metadata jsonb,p_runtime text) RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT CASE WHEN NOT COALESCE(
 jsonb_typeof(p_metadata)='object' AND p_runtime ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' AND length(p_runtime)<=50
 AND jsonb_typeof(p_metadata->'minRuntimeVersion')='string' AND length(p_metadata->>'minRuntimeVersion')<=50
 AND p_metadata->>'minRuntimeVersion' ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
 AND jsonb_typeof(p_metadata->'protocolVersion')='string' AND length(p_metadata->>'protocolVersion') BETWEEN 1 AND 50
 AND jsonb_typeof(p_metadata->'manifestSchemaVersion')='string' AND length(p_metadata->>'manifestSchemaVersion') BETWEEN 1 AND 50
 AND jsonb_typeof(p_metadata->'buildTarget')='string' AND length(p_metadata->>'buildTarget') BETWEEN 1 AND 100
 AND jsonb_typeof(p_metadata->'requiredCapabilities')='array',false) THEN false ELSE
 string_to_array(p_runtime,'.')::numeric[] >= string_to_array(p_metadata->>'minRuntimeVersion','.')::numeric[] AND EXISTS(
 SELECT 1 FROM runtime_compatibility_catalog c WHERE c.is_active AND c.runtime_version=p_runtime AND c.protocol_version=p_metadata->>'protocolVersion' AND c.manifest_schema_version=p_metadata->>'manifestSchemaVersion' AND c.capabilities @> (p_metadata->'requiredCapabilities')) END
$$;
CREATE FUNCTION playtest_lifecycle_gate(p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_building uuid,p_input jsonb,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;s scenarios;d scenario_drafts;v scenario_versions;a revision_artifacts;m revision_artifacts;vr validation_runs;
 session playtest_sessions;pin playtest_package_pins;receipt playtest_command_receipts;ent service_entitlements;h text;result jsonb;new_id uuid;stamp timestamptz;package jsonb;
BEGIN
 IF p_action NOT IN('Prepare','Start') THEN RETURN jsonb_build_object('code','PLAYTEST_ACTION_INVALID','status',400);END IF;
 IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL AND (registration_expires_at IS NULL OR email_verified_at IS NOT NULL);
 IF actor.id IS NULL OR NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
 IF p_action='Prepare' THEN SELECT * INTO s FROM scenarios WHERE id=p_resource;SELECT * INTO b FROM buildings WHERE id=s.building_id;
 ELSE SELECT * INTO session FROM playtest_sessions WHERE id=p_resource AND created_by=p_actor;SELECT * INTO b FROM buildings WHERE id=session.building_id;END IF;
 IF b.id IS NULL OR b.organization_id IS DISTINCT FROM actor.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL OR (p_building IS NOT NULL AND p_building<>b.id) OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 -- Time-based session checks must use current time after all potentially blocking locks.
 IF NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
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
  result:=jsonb_build_object('code','OK','id',new_id,'scenarioVersionId',v.id,'artifactId',a.id,'validationRunId',vr.id,'packageHash',a.sha256_hash,'manifestHash',m.sha256_hash,'buildTarget',a.metadata->>'buildTarget');
 ELSE
  SELECT * INTO session FROM playtest_sessions WHERE id=p_resource AND created_by=p_actor FOR UPDATE;
  SELECT * INTO pin FROM playtest_package_pins WHERE playtest_id=session.id FOR UPDATE;
  IF pin.playtest_id IS NULL OR pin.prepared_family<>p_family THEN RETURN jsonb_build_object('code','PLAYTEST_SESSION_MISMATCH','status',409);END IF;
  IF session.status<>'Created' THEN RETURN jsonb_build_object('code','PLAYTEST_ALREADY_STARTED','status',409);END IF;
  IF package_runtime_compatible(pin.snapshot->'metadata',p_input->>'runtimeVersion') IS NOT TRUE THEN RETURN jsonb_build_object('code','RUNTIME_INCOMPATIBLE','status',409);END IF;
  IF NOT EXISTS(SELECT 1 FROM processing_job_attempts pa JOIN processing_jobs j ON j.id=pa.processing_job_id AND j.current_attempt_id=pa.id JOIN revision_artifacts accepted_art ON accepted_art.id=pin.artifact_id AND accepted_art.attempt_id=pa.id WHERE pa.id=pin.attempt_id AND pa.status='Succeeded' AND j.status='Succeeded' AND accepted_art.is_runtime_ready) THEN RETURN jsonb_build_object('code','PLAYTEST_PACKAGE_REQUIRED','status',409);END IF;
  stamp:=clock_timestamp();
  SELECT e.* INTO ent FROM service_entitlements e WHERE e.organization_id=b.organization_id AND e.building_id=b.id AND e.starts_at<=stamp AND e.ends_at>stamp AND
   ((e.status='Active' AND EXISTS(SELECT 1 FROM payment_transactions tx JOIN payos_payment_requests pr ON pr.id=tx.payment_request_id JOIN quotations q ON q.id=pr.quotation_id WHERE tx.id=e.payment_transaction_id AND tx.status='Applied' AND q.billing_purpose='BuildingService' AND q.organization_id=b.organization_id)) OR (e.status='Trial' AND e.playtest_units_used<e.playtest_units_granted))
   ORDER BY CASE WHEN e.status='Active' THEN 0 ELSE 1 END,e.ends_at DESC,e.id LIMIT 1 FOR UPDATE;
  IF NOT EXISTS(SELECT 1 FROM auth_refresh_tokens WHERE user_id=p_actor AND family_id=p_family AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>clock_timestamp()) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
  IF ent.id IS NULL OR ent.ends_at<=clock_timestamp() THEN RETURN jsonb_build_object('code','PLAYTEST_ENTITLEMENT_REQUIRED','status',409);END IF;
  stamp:=clock_timestamp(); -- Issue the full five-minute grant only after entitlement locking completes.
  IF ent.status='Trial' THEN UPDATE service_entitlements SET playtest_units_used=playtest_units_used+1 WHERE id=ent.id;END IF;
  UPDATE playtest_sessions SET service_entitlement_id=ent.id,status='Running',started_at=stamp,runtime_version=p_input->>'runtimeVersion',start_idempotency_key='start:'||p_actor||':'||p_key WHERE id=session.id;
  UPDATE playtest_package_pins SET started_family=p_family,grant_issued_at=stamp,grant_expires_at=stamp+interval '5 minutes' WHERE playtest_id=session.id;
  result:=jsonb_build_object('code','OK','playtestId',session.id,'actorId',p_actor,'familyId',p_family,'scenarioVersionId',session.scenario_version_id,'artifactId',pin.artifact_id,'validationRunId',pin.validation_run_id,'packageHash',pin.snapshot->>'packageHash','manifestHash',pin.snapshot->>'manifestHash','buildTarget',pin.snapshot->>'buildTarget','packageKey',pin.snapshot->>'packageKey','manifestKey',pin.snapshot->>'manifestKey','protocolVersion',session.protocol_version,'manifestSchemaVersion',session.manifest_schema_version,'runtimeVersion',p_input->>'runtimeVersion','issuedAt',stamp,'expiresAt',stamp+interval '5 minutes');
 END IF;
 INSERT INTO playtest_command_receipts(actor_id,operation,idempotency_key,input_hash,result) VALUES(p_actor,p_action,p_key,h,result);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create','PlaytestSession',CASE WHEN p_action='Prepare' THEN new_id ELSE session.id END,gen_random_uuid(),jsonb_build_object('operation',p_action,'scenarioVersionId',CASE WHEN p_action='Prepare' THEN v.id ELSE session.scenario_version_id END),now());
 RETURN result;
END $$;
DO $$ DECLARE os boolean;oi boolean;changed boolean;had_create boolean;sig text;r text;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');had_create:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 IF changed THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user);END IF;GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['playtest_lifecycle_gate(text,uuid,uuid,uuid,uuid,jsonb,text)','package_runtime_compatible(jsonb,text)'] LOOP EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION playtest_lifecycle_gate(text,uuid,uuid,uuid,uuid,jsonb,text) TO %I',r);EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON playtest_sessions,playtest_package_pins,playtest_command_receipts FROM %I',r);END IF;END LOOP;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF changed THEN IF os IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $$;
