-- Additive authoring receipts, immutable v7 snapshots. Legacy versions stay without rubric/proof.
LOCK TABLE scenario_drafts,scenario_versions IN SHARE ROW EXCLUSIVE MODE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM scenario_drafts GROUP BY scenario_id,draft_number HAVING count(*)>1) OR EXISTS(SELECT 1 FROM scenario_versions GROUP BY scenario_id,version_number HAVING count(*)>1) THEN RAISE EXCEPTION 'Authoring preflight: duplicate draft/version numbers require operator review'; END IF;
END $$;
CREATE UNIQUE INDEX scenario_draft_number_key ON scenario_drafts(scenario_id,draft_number);
ALTER TABLE scenario_versions ADD COLUMN state_snapshot jsonb,ADD COLUMN learning_objectives jsonb,ADD COLUMN learner_instructions text,ADD COLUMN rubric jsonb;
ALTER TABLE processing_jobs ADD COLUMN input_snapshot jsonb;
CREATE TABLE authoring_command_receipts (
 actor_id uuid NOT NULL REFERENCES users(id),operation text NOT NULL,idempotency_key varchar(128) NOT NULL,
 input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[0-9a-f]{64}$'),result_id uuid NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(actor_id,operation,idempotency_key)
);
ALTER TABLE authoring_command_receipts ENABLE ROW LEVEL SECURITY;
CREATE POLICY authoring_owner_access ON authoring_command_receipts TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
GRANT SELECT,INSERT ON authoring_command_receipts TO fet3d_ifc_upload_owner;
GRANT SELECT,INSERT,UPDATE ON scenarios,scenario_drafts TO fet3d_ifc_upload_owner;
GRANT SELECT,INSERT ON scenario_versions TO fet3d_ifc_upload_owner;
GRANT SELECT ON runtime_compatibility_catalog TO fet3d_ifc_upload_owner;
CREATE POLICY authoring_owner_access ON scenarios TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
CREATE POLICY authoring_owner_access ON scenario_drafts TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
CREATE POLICY authoring_owner_access ON runtime_compatibility_catalog TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);

CREATE FUNCTION immutable_scenario_snapshot() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN RAISE EXCEPTION 'Scenario versions are immutable; author a new version'; END $$;
CREATE TRIGGER immutable_scenario_snapshot BEFORE UPDATE OR DELETE ON scenario_versions FOR EACH ROW EXECUTE FUNCTION immutable_scenario_snapshot();
CREATE FUNCTION immutable_processing_input() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
BEGIN IF NEW.input_hash IS DISTINCT FROM OLD.input_hash OR NEW.input_snapshot IS DISTINCT FROM OLD.input_snapshot OR NEW.source_document_id IS DISTINCT FROM OLD.source_document_id OR NEW.scenario_version_id IS DISTINCT FROM OLD.scenario_version_id OR NEW.kind IS DISTINCT FROM OLD.kind THEN RAISE EXCEPTION 'Processing inputs are immutable'; END IF; RETURN NEW; END $$;
CREATE TRIGGER immutable_processing_input BEFORE UPDATE ON processing_jobs FOR EACH ROW EXECUTE FUNCTION immutable_processing_input();

CREATE FUNCTION scenario_reference_issues(p_revision uuid,p_state jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE issues jsonb:='[]';anchor text;cap text;available jsonb;catalog runtime_compatibility_catalog;
BEGIN
 IF p_state->'objectAnchors' IS NOT NULL AND p_state->'objectAnchors'<>'null'::jsonb THEN
  IF jsonb_typeof(p_state->'objectAnchors') IS DISTINCT FROM 'array' THEN RETURN jsonb_build_array(jsonb_build_object('code','ANCHORS_INVALID','path','$.objectAnchors','message','Anchors must be an array.'));END IF;
  SELECT metadata->'objectAnchors' INTO available FROM revision_artifacts a JOIN processing_jobs j ON j.id=a.job_id JOIN processing_job_attempts pa ON pa.id=a.attempt_id AND j.current_attempt_id=pa.id
   WHERE a.revision_id=p_revision AND j.kind='Geometry' AND j.status='Succeeded' AND pa.status='Succeeded' AND jsonb_typeof(metadata->'objectAnchors')='array' ORDER BY a.created_at DESC,a.id LIMIT 1;
  FOR anchor IN SELECT jsonb_array_elements_text(p_state->'objectAnchors') LOOP
   IF available IS NULL OR NOT available ? anchor THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','ANCHOR_NOT_FOUND','path','$.objectAnchors','message','Anchor does not exist in accepted geometry for this revision.'));END IF;
  END LOOP;
 END IF;
 IF p_state->'requiredCapabilities' IS NOT NULL AND p_state->'requiredCapabilities'<>'null'::jsonb THEN
  IF jsonb_typeof(p_state->'requiredCapabilities') IS DISTINCT FROM 'array' THEN RETURN issues||jsonb_build_array(jsonb_build_object('code','CAPABILITIES_INVALID','path','$.requiredCapabilities','message','Capabilities must be an array.'));END IF;
  IF jsonb_array_length(p_state->'requiredCapabilities')>0 THEN
   SELECT * INTO catalog FROM runtime_compatibility_catalog WHERE runtime_version=p_state->>'runtimeVersion' AND is_active ORDER BY created_at DESC,id LIMIT 1;
   IF catalog.id IS NULL THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','RUNTIME_NOT_SUPPORTED','path','$.runtimeVersion','message','Select an active runtime in the server catalog.'));
   ELSE FOR cap IN SELECT jsonb_array_elements_text(p_state->'requiredCapabilities') LOOP
    IF NOT catalog.capabilities ? cap THEN issues:=issues||jsonb_build_array(jsonb_build_object('code','CAPABILITY_NOT_SUPPORTED','path','$.requiredCapabilities','message','Runtime does not provide this capability.'));END IF;
   END LOOP;END IF;
  END IF;
 END IF;
 RETURN issues;
END $$;

CREATE FUNCTION scenario_authoring_gate(p_action text,p_actor uuid,p_resource uuid,p_input jsonb,p_key text DEFAULT NULL,p_revision bigint DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;b buildings;s scenarios;d scenario_drafts;v scenario_versions;r revisions;src source_documents;receipt authoring_command_receipts;
 h text:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input,'revision',p_revision));result_uuid uuid;n integer;rev bigint;job uuid;pin jsonb;issues jsonb;
BEGIN
 IF p_action<>'UpdateDraft' AND (p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]') THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400); END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF actor.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 IF p_action='CreateScenario' THEN SELECT * INTO b FROM buildings WHERE id=p_resource;
 ELSIF p_action='CreateDraft' THEN SELECT * INTO s FROM scenarios WHERE id=p_resource;SELECT * INTO b FROM buildings WHERE id=s.building_id;
 ELSIF p_action='BuildPackage' THEN SELECT * INTO v FROM scenario_versions WHERE id=p_resource;SELECT * INTO s FROM scenarios WHERE id=v.scenario_id;SELECT * INTO b FROM buildings WHERE id=v.building_id;
 ELSE SELECT * INTO d FROM scenario_drafts WHERE id=p_resource;SELECT * INTO s FROM scenarios WHERE id=d.scenario_id;SELECT * INTO b FROM buildings WHERE id=d.building_id; END IF;
 IF b.id IS NULL OR NOT b.is_active OR b.deleted_at IS NOT NULL OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) OR (actor.role='OrganizationUser' AND actor.organization_id IS DISTINCT FROM b.organization_id) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF p_key IS NOT NULL THEN PERFORM pg_advisory_xact_lock(hashtextextended('authoring:'||p_actor||':'||p_action||':'||p_key,0)); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 IF s.id IS NOT NULL THEN SELECT * INTO s FROM scenarios WHERE scenarios.id=s.id FOR UPDATE; END IF;
 SELECT * INTO receipt FROM authoring_command_receipts WHERE actor_id=p_actor AND operation=p_action AND idempotency_key=p_key;
 IF receipt.result_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409); END IF;
  RETURN jsonb_build_object('code','OK','id',receipt.result_id);
 END IF;
 IF p_action='CreateScenario' THEN
  IF NULLIF(btrim(p_input->>'name'),'') IS NULL OR length(p_input->>'name')>255 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('name',jsonb_build_array('Name must contain 1-255 characters.'))); END IF;
  result_uuid:=gen_random_uuid();INSERT INTO scenarios(id,building_id,organization_id,name,created_by,created_at) VALUES(result_uuid,b.id,b.organization_id,btrim(p_input->>'name'),p_actor,now());
 ELSIF p_action='CreateDraft' THEN
  SELECT * INTO r FROM revisions WHERE revisions.id=(p_input->>'revisionId')::uuid AND building_id=b.id AND organization_id=b.organization_id;
  IF r.id IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('revisionId',jsonb_build_array('Revision must belong to this Building.'))); END IF;
  SELECT COALESCE(max(draft_number),0)+1 INTO n FROM scenario_drafts WHERE scenario_id=s.id;
  result_uuid:=gen_random_uuid();INSERT INTO scenario_drafts(id,scenario_id,revision_id,organization_id,building_id,draft_number,state,source,created_by,created_at,updated_at) VALUES(result_uuid,s.id,r.id,b.organization_id,b.id,n,'{}','manual',p_actor,now(),now());
 ELSIF p_action IN('UpdateDraft','Snapshot') THEN
  SELECT * INTO d FROM scenario_drafts WHERE scenario_drafts.id=p_resource FOR UPDATE;
  SELECT xmin::text::bigint INTO rev FROM scenario_drafts WHERE scenario_drafts.id=p_resource;
  IF p_revision IS NULL THEN RETURN jsonb_build_object('code','PRECONDITION_REQUIRED','status',428); END IF;
  IF rev<>p_revision THEN RETURN jsonb_build_object('code','PRECONDITION_FAILED','status',412); END IF;
  IF p_action='UpdateDraft' THEN
   UPDATE scenario_drafts SET state=p_input,updated_at=now() WHERE scenario_drafts.id=d.id RETURNING xmin::text::bigint INTO rev;
   INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Update','ScenarioDraft',d.id,gen_random_uuid(),jsonb_build_object('revision',rev,'stateHash',fet3d_jsonb_payload_hash(p_input)),now());
   RETURN jsonb_build_object('code','OK','revision',rev);
  END IF;
  IF jsonb_typeof(d.state->'rubric') IS DISTINCT FROM 'object' OR jsonb_typeof(d.state->'learningObjectives') IS DISTINCT FROM 'array' OR jsonb_array_length(d.state->'learningObjectives')=0 OR NULLIF(btrim(d.state->>'learnerInstructions'),'') IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('state',jsonb_build_array('Validate the v7 rubric and learner fields before snapshot.'))); END IF;
  issues:=scenario_reference_issues(d.revision_id,d.state);
  IF jsonb_array_length(issues)>0 THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400,'errors',jsonb_build_object('state',jsonb_build_array('Anchor or runtime capability is not accepted for this revision.')));END IF;
  SELECT COALESCE(max(version_number),0)+1 INTO n FROM scenario_versions WHERE scenario_id=s.id;
  result_uuid:=gen_random_uuid();
  INSERT INTO scenario_versions(id,scenario_id,revision_id,building_id,organization_id,version_number,name,schema_version,algorithm_version,random_seed,time_limit_seconds,spawn_config,goal_config,fire_source_config,npc_config,blocked_elements,routing_config,scoring_config,mode_policy,safety_thresholds,replan_interval_seconds,scenario_hash,created_by,created_at,state_snapshot,learning_objectives,learner_instructions,rubric)
   VALUES(result_uuid,s.id,d.revision_id,b.id,b.organization_id,n,COALESCE(NULLIF(d.state->>'name',''),s.name),'7','1',COALESCE((d.state->>'randomSeed')::bigint,0),(d.state->'scoringConfig'->>'timeLimitSeconds')::integer,d.state->'spawnPoints',COALESCE(NULLIF(d.state->'goals','null'::jsonb),'[]'),d.state->'hazards',COALESCE(NULLIF(d.state->'npcs','null'::jsonb),'[]'),COALESCE(NULLIF(d.state->'blockedElements','null'::jsonb),'[]'),d.state->'routingConfig',d.state->'scoringConfig',COALESCE(NULLIF(d.state->'modePolicy','null'::jsonb),'{}'),COALESCE(NULLIF(d.state->'safetyThresholds','null'::jsonb),'{}'),COALESCE((d.state->>'replanIntervalSeconds')::integer,0),fet3d_jsonb_payload_hash(d.state),p_actor,now(),d.state,d.state->'learningObjectives',d.state->>'learnerInstructions',d.state->'rubric');
 ELSIF p_action='BuildPackage' THEN
  IF v.state_snapshot IS NULL THEN RETURN jsonb_build_object('code','SCENARIO_SNAPSHOT_REQUIRED','status',409); END IF;
  SELECT * INTO src FROM source_documents WHERE revision_id=v.revision_id AND upload_verified_at IS NOT NULL;
  IF src.id IS NULL THEN RETURN jsonb_build_object('code','IFC_SOURCE_NOT_VERIFIED','status',422); END IF;
  IF p_input->>'kind' IS NULL OR p_input->>'kind' NOT IN('PlaytestPackage','ReleasePackage') OR NULLIF(p_input->>'buildTarget','') IS NULL THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400); END IF;
  pin:=jsonb_build_object('scenarioVersionId',v.id,'scenarioHash',v.scenario_hash,'rubricHash',fet3d_jsonb_payload_hash(v.rubric),'sourceId',src.id,'sourceHash',src.sha256_hash,'kind',p_input->>'kind','buildTarget',p_input->>'buildTarget');
  SELECT processing_jobs.id INTO result_uuid FROM processing_jobs WHERE revision_id=v.revision_id AND scenario_version_id=v.id AND kind=p_input->>'kind' AND input_hash=fet3d_jsonb_payload_hash(pin);
  IF result_uuid IS NULL THEN
   result_uuid:=gen_random_uuid();INSERT INTO processing_jobs(id,revision_id,source_document_id,scenario_version_id,kind,job_key,input_hash,input_snapshot,status,created_at) VALUES(result_uuid,v.revision_id,src.id,v.id,p_input->>'kind',gen_random_uuid(),fet3d_jsonb_payload_hash(pin),pin,'Queued',now());
   PERFORM enqueue_integration_outbox_event('package-build:'||result_uuid,'ProcessingJob',result_uuid,'ProcessingJobRequested','1',jsonb_build_object('job_id',result_uuid,'input_hash',fet3d_jsonb_payload_hash(pin),'input_snapshot',pin));
  END IF;
 ELSE RETURN jsonb_build_object('code','AUTHORING_ACTION_INVALID','status',400); END IF;
 INSERT INTO authoring_command_receipts(actor_id,operation,idempotency_key,input_hash,result_id) VALUES(p_actor,p_action,p_key,h,result_uuid);
 INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create',CASE p_action WHEN 'CreateScenario' THEN 'Scenario' WHEN 'CreateDraft' THEN 'ScenarioDraft' WHEN 'Snapshot' THEN 'ScenarioVersion' ELSE 'ProcessingJob' END,result_uuid,gen_random_uuid(),jsonb_build_object('operation',p_action),now());
 RETURN jsonb_build_object('code','OK','id',result_uuid);
END $$;

CREATE FUNCTION processing_worker_context(p_attempt uuid,p_token uuid) RETURNS jsonb
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('inputSnapshot',j.input_snapshot,'scenarioSnapshot',v.state_snapshot) FROM processing_job_attempts a JOIN processing_jobs j ON j.id=a.processing_job_id AND j.current_attempt_id=a.id LEFT JOIN scenario_versions v ON v.id=j.scenario_version_id
 WHERE a.id=p_attempt AND a.lease_token=p_token AND (a.status='Succeeded' OR a.status='Running' AND a.lease_until>clock_timestamp())
$$;
CREATE FUNCTION enforce_package_output_contract() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE job processing_jobs;BEGIN
 SELECT * INTO job FROM processing_jobs WHERE id=NEW.job_id;
 IF job.kind IN('PlaytestPackage','ReleasePackage') AND NEW.artifact_type='unity_package' AND
 (NEW.metadata->>'buildTarget' IS DISTINCT FROM job.input_snapshot->>'buildTarget' OR NULLIF(NEW.metadata->>'minRuntimeVersion','') IS NULL OR NEW.metadata->>'minRuntimeVersion' !~ '^[0-9]+\.[0-9]+\.[0-9]+$' OR NULLIF(NEW.metadata->>'protocolVersion','') IS NULL OR NULLIF(NEW.metadata->>'manifestSchemaVersion','') IS NULL OR jsonb_typeof(NEW.metadata->'requiredCapabilities') IS DISTINCT FROM 'array')
 THEN RAISE EXCEPTION 'Package output metadata does not match pinned input/runtime contract'; END IF;
 RETURN NEW;END $$;
CREATE TRIGGER package_output_contract BEFORE INSERT ON revision_artifacts FOR EACH ROW EXECUTE FUNCTION enforce_package_output_contract();

DO $$ DECLARE os boolean;oi boolean;changed boolean;had_create boolean;sig text;r text;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');had_create:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');
 IF changed THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user);END IF;GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 FOREACH sig IN ARRAY ARRAY['scenario_authoring_gate(text,uuid,uuid,jsonb,text,bigint)','processing_worker_context(uuid,uuid)','scenario_reference_issues(uuid,jsonb)'] LOOP EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);END LOOP;
 GRANT EXECUTE ON FUNCTION processing_worker_context(uuid,uuid) TO fet3d_processing_executor;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION scenario_authoring_gate(text,uuid,uuid,jsonb,text,bigint),scenario_reference_issues(uuid,jsonb) TO %I',r);EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON scenarios,scenario_drafts,scenario_versions FROM %I',r);END IF;END LOOP;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF changed THEN IF os IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $$;
