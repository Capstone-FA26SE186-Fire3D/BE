-- IFC-03 (backend part, Docs ifc_ingestion_visual_interaction.md 5.9 and workflows 3): worker issues name their IFC elements
-- (ifcGlobalIds -> revision_issues.ifc_guid + details), a failed Geometry QA moves the revision to NeedsFix, and the v7 bim_facts table
-- is created and filled with IfcSpace Name/LongName/Category from building_metadata.spaces of an accepted run, so
-- GET /api/revisions/{id}/bim-facts works on a database built from migrations. The worker gate body is the one from
-- AddEquipmentCandidates with only these three changes.
CREATE TEMP TABLE ifc_qa_contract_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO ifc_qa_contract_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

-- v7 definition (Docs fire_evacuation_schema.sql).
CREATE TABLE IF NOT EXISTS bim_facts (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    revision_id     UUID REFERENCES revisions(id) ON DELETE CASCADE NOT NULL,
    ifc_global_id   VARCHAR(255) NOT NULL,
    entity_type     VARCHAR(100) NOT NULL,
    property_path   TEXT NOT NULL,
    value           JSONB,
    source_hash     VARCHAR(64) NOT NULL,
    quality_flags   JSONB NOT NULL DEFAULT '[]',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (revision_id, ifc_global_id, property_path)
);
CREATE INDEX IF NOT EXISTS idx_bim_facts_revision ON bim_facts(revision_id);
CREATE INDEX IF NOT EXISTS idx_bim_facts_ifc_global_id ON bim_facts(revision_id, ifc_global_id);
ALTER TABLE bim_facts ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON bim_facts FROM PUBLIC;

CREATE OR REPLACE FUNCTION processing_worker_gate_before_streams(p_action text,p_job uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE j processing_jobs;a processing_job_attempts;e integration_outbox_events;receipt processing_delivery_receipts;
 b buildings;aid uuid;token uuid;rid uuid;outid uuid;v uuid;outrow record;summary jsonb;h text;k text;issue jsonb;primary_art uuid;
BEGIN
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO j FROM processing_jobs WHERE id=p_job;
 SELECT b0.* INTO b FROM revisions r JOIN buildings b0 ON b0.id=r.building_id WHERE r.id=j.revision_id;
 IF j.id IS NULL OR b.id IS NULL THEN RETURN jsonb_build_object('code','JOB_NOT_FOUND','status',404); END IF;
 IF j.kind NOT IN('Geometry','PlaytestPackage','ReleasePackage') OR NOT EXISTS(SELECT 1 FROM source_documents s WHERE s.id=j.source_document_id AND s.revision_id=j.revision_id AND s.upload_verified_at IS NOT NULL AND s.source_etag IS NOT NULL)
 THEN RETURN jsonb_build_object('code','IFC_SOURCE_NOT_VERIFIED','status',422); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','JOB_SCOPE_UNAVAILABLE','status',409); END IF;
 IF p_action='Claim' THEN
  SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=p_input->>'eventKey' FOR UPDATE;
  IF e.aggregate_id IS DISTINCT FROM j.id OR e.payload_hash IS DISTINCT FROM p_input->>'payloadHash' OR e.schema_version<>'1' OR e.event_type NOT IN('ProcessingJobRequested','ProcessingJobRequeue') THEN RETURN jsonb_build_object('code','DELIVERY_CONFLICT','status',409); END IF;
 END IF;
 SELECT * INTO j FROM processing_jobs WHERE id=p_job FOR UPDATE;
 IF p_action='Claim' THEN
  SELECT * INTO receipt FROM processing_delivery_receipts WHERE event_key=e.idempotency_key;
  IF receipt.attempt_id IS NOT NULL THEN
   SELECT * INTO a FROM processing_job_attempts WHERE id=receipt.attempt_id;
   IF receipt.payload_hash<>e.payload_hash THEN RETURN jsonb_build_object('code','DELIVERY_CONFLICT','status',409); END IF;
   IF a.id IS DISTINCT FROM j.current_attempt_id OR a.status='Expired' THEN RETURN jsonb_build_object('code','ATTEMPT_STALE','status',409); END IF;
   RETURN jsonb_build_object('code','OK','replayed',true,'status',a.status,'attemptId',a.id,'leaseToken',a.lease_token,'leaseUntil',a.lease_until,'receiptId',receipt.receipt_id,'outputPrefix','worker-output/'||b.organization_id||'/'||j.id||'/'||a.id||'/','inputHash',j.input_hash,'sourceKey',(SELECT storage_url FROM source_documents WHERE id=j.source_document_id),'sourceHash',(SELECT sha256_hash FROM source_documents WHERE id=j.source_document_id),'scenarioVersionId',j.scenario_version_id);
  END IF;
  IF j.status NOT IN('Queued','Running') THEN RETURN jsonb_build_object('code','JOB_NOT_CLAIMABLE','status',409); END IF;
  IF p_input->>'inputHash' IS DISTINCT FROM j.input_hash OR NULLIF(p_input->>'toolchainVersion','') IS NULL THEN RETURN jsonb_build_object('code','INPUT_PROVENANCE_CONFLICT','status',409); END IF;
  SELECT * INTO a FROM processing_job_attempts WHERE id=j.current_attempt_id;
  IF a.status='Running' AND a.lease_until>clock_timestamp() THEN RETURN jsonb_build_object('code','JOB_BUSY','status',409); END IF;
  IF a.status='Running' THEN UPDATE processing_job_attempts SET status='Expired',finished_at=clock_timestamp() WHERE id=a.id; END IF;
  aid:=gen_random_uuid();token:=gen_random_uuid();rid:=gen_random_uuid();
  INSERT INTO processing_job_attempts(id,processing_job_id,attempt_number,input_hash,toolchain_version,lease_owner,lease_token,lease_until,status)
   SELECT aid,j.id,COALESCE(max(attempt_number),0)+1,j.input_hash,p_input->>'toolchainVersion','http-worker',token,clock_timestamp()+interval '60 seconds','Running' FROM processing_job_attempts WHERE processing_job_id=j.id;
  UPDATE processing_jobs SET current_attempt_id=aid,status='Running',started_at=COALESCE(started_at,clock_timestamp()),heartbeat_at=clock_timestamp() WHERE id=j.id;
  INSERT INTO processing_delivery_receipts(event_key,payload_hash,attempt_id,receipt_id) VALUES(e.idempotency_key,e.payload_hash,aid,rid);
  RETURN jsonb_build_object('code','OK','replayed',false,'status','Running','attemptId',aid,'leaseToken',token,'leaseUntil',clock_timestamp()+interval '60 seconds','receiptId',rid,'outputPrefix','worker-output/'||b.organization_id||'/'||j.id||'/'||aid||'/','inputHash',j.input_hash,'sourceKey',(SELECT storage_url FROM source_documents WHERE id=j.source_document_id),'sourceHash',(SELECT sha256_hash FROM source_documents WHERE id=j.source_document_id),'scenarioVersionId',j.scenario_version_id);
 END IF;
 SELECT * INTO a FROM processing_job_attempts WHERE id=(p_input->>'attemptId')::uuid AND processing_job_id=j.id FOR UPDATE;
 IF a.id IS NULL OR a.id IS DISTINCT FROM j.current_attempt_id OR a.lease_token IS DISTINCT FROM (p_input->>'leaseToken')::uuid THEN RETURN jsonb_build_object('code','ATTEMPT_STALE','status',409); END IF;
 IF p_action='Complete' AND a.status='Succeeded' THEN
  IF a.output_hash=p_input->>'outputHash' THEN RETURN jsonb_build_object('code','OK','outputHash',a.output_hash); END IF;
  RETURN jsonb_build_object('code','OUTPUT_CONFLICT','status',409);
 END IF;
 IF a.status<>'Running' OR a.lease_until<=clock_timestamp() THEN RETURN jsonb_build_object('code','ATTEMPT_STALE','status',409); END IF;
 IF p_action='Renew' THEN
  UPDATE processing_job_attempts SET lease_until=clock_timestamp()+interval '60 seconds' WHERE id=a.id;
  UPDATE processing_jobs SET heartbeat_at=clock_timestamp() WHERE id=j.id;
  RETURN jsonb_build_object('code','OK','leaseUntil',clock_timestamp()+interval '60 seconds');
 ELSIF p_action='Output' THEN
  summary:=p_input->'output';h:=fet3d_jsonb_payload_hash(summary);k:='worker-output/'||b.organization_id||'/'||j.id||'/'||a.id||'/';
  IF summary->>'objectKey' NOT LIKE k||'%' OR summary->>'objectKey' ~ '(^|/)\.\.(/|$)' OR summary->>'sha256Hash' !~ '^[0-9a-f]{64}$' OR (summary->>'sizeBytes')::bigint<=0
   OR summary->>'outcome' NOT IN('Passed','Failed') OR NULLIF(summary->>'validatorVersion','') IS NULL OR jsonb_typeof(summary->'metadata')<>'object' OR jsonb_typeof(summary->'issues')<>'array'
   OR NULLIF(summary->>'artifactType','') IS NULL OR NULLIF(summary->>'schemaVersion','') IS NULL
  THEN RETURN jsonb_build_object('code','OUTPUT_INVALID','status',400); END IF;
  FOR issue IN SELECT value FROM jsonb_array_elements(summary->'issues') LOOP
   IF issue->>'severity' NOT IN('Info','Warning','Error','Critical') OR NULLIF(issue->>'code','') IS NULL OR NULLIF(issue->>'message','') IS NULL THEN RETURN jsonb_build_object('code','OUTPUT_INVALID','status',400); END IF;
  END LOOP;
  SELECT artifact_id INTO outid FROM processing_candidate_outputs WHERE attempt_id=a.id AND artifact_type=summary->>'artifactType';
  IF outid IS NOT NULL THEN
   IF NOT EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND artifact_type=summary->>'artifactType' AND input_hash=h) THEN RETURN jsonb_build_object('code','OUTPUT_CONFLICT','status',409); END IF;
  ELSE
   outid:=gen_random_uuid(); INSERT INTO processing_candidate_outputs(attempt_id,artifact_type,input_hash,artifact_id,output) VALUES(a.id,summary->>'artifactType',h,outid,summary);
  END IF;
  SELECT fet3d_jsonb_payload_hash(jsonb_agg(output ORDER BY artifact_type)) INTO h FROM processing_candidate_outputs WHERE attempt_id=a.id;
  RETURN jsonb_build_object('code','OK','artifactId',outid,'outputHash',h);
 ELSIF p_action='Complete' THEN
  SELECT fet3d_jsonb_payload_hash(jsonb_agg(output ORDER BY artifact_type)) INTO h FROM processing_candidate_outputs WHERE attempt_id=a.id;
  IF h IS NULL OR h IS DISTINCT FROM p_input->>'outputHash' THEN RETURN jsonb_build_object('code','OUTPUT_CONFLICT','status',409); END IF;
  IF j.kind IN('PlaytestPackage','ReleasePackage') AND (NOT EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND artifact_type='unity_package') OR NOT EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND artifact_type='manifest')) THEN RETURN jsonb_build_object('code','PACKAGE_OUTPUT_INCOMPLETE','status',409); END IF;
  v:=gen_random_uuid();
  FOR outrow IN SELECT * FROM processing_candidate_outputs WHERE attempt_id=a.id ORDER BY artifact_type LOOP
   INSERT INTO revision_artifacts(id,revision_id,job_id,artifact_type,object_key,sha256_hash,size_bytes,schema_version,metadata,created_at,attempt_id,is_runtime_ready)
    VALUES(outrow.artifact_id,j.revision_id,j.id,outrow.artifact_type,outrow.output->>'objectKey',outrow.output->>'sha256Hash',(outrow.output->>'sizeBytes')::bigint,outrow.output->>'schemaVersion',outrow.output->'metadata',now(),a.id,j.kind IN('PlaytestPackage','ReleasePackage') AND outrow.output->>'outcome'='Passed');
   IF primary_art IS NULL OR outrow.artifact_type IN('unity_package','preview_glb') THEN primary_art:=outrow.artifact_id; summary:=outrow.output; END IF;
  END LOOP;
  INSERT INTO validation_runs(id,revision_id,job_id,kind,scenario_version_id,candidate_artifact_id,outcome,scenario_hash,validator_version,report,created_at,processing_attempt_id,started_at,finished_at)
   VALUES(v,j.revision_id,j.id,j.kind,j.scenario_version_id,primary_art,CASE WHEN EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND (output->>'outcome'='Failed' OR EXISTS(SELECT 1 FROM jsonb_array_elements(output->'issues') i WHERE i->>'severity' IN('Error','Critical')))) THEN 'Failed' ELSE 'Passed' END,
    CASE WHEN j.scenario_version_id IS NULL THEN NULL ELSE (SELECT scenario_hash FROM scenario_versions WHERE id=j.scenario_version_id) END,summary->>'validatorVersion',jsonb_build_object('outputHash',h,'toolchainVersion',a.toolchain_version),now(),a.id,a.started_at,clock_timestamp());
  FOR issue IN SELECT value FROM processing_candidate_outputs o CROSS JOIN LATERAL jsonb_array_elements(o.output->'issues') WHERE o.attempt_id=a.id LOOP
   INSERT INTO revision_issues(id,validation_run_id,severity,code,ifc_guid,message,details,created_at) VALUES(gen_random_uuid(),v,issue->>'severity',issue->>'code',
    CASE WHEN jsonb_typeof(issue->'ifcGlobalIds')='array' THEN issue->'ifcGlobalIds'->>0 END,issue->>'message',
    COALESCE(issue->'details','{}'::jsonb)||CASE WHEN jsonb_typeof(issue->'ifcGlobalIds')='array' THEN jsonb_build_object('ifcGlobalIds',issue->'ifcGlobalIds') ELSE '{}'::jsonb END,now());
  END LOOP;
  UPDATE processing_job_attempts SET status='Succeeded',output_hash=h,finished_at=clock_timestamp() WHERE id=a.id;
  UPDATE processing_jobs SET status='Succeeded',finished_at=clock_timestamp(),error_message=NULL WHERE id=j.id;
  IF j.kind='Geometry' THEN
   UPDATE revisions SET status='ReadyForScenario',updated_at=now() WHERE id=j.revision_id AND EXISTS(SELECT 1 FROM validation_runs WHERE id=v AND outcome='Passed');
   UPDATE revisions SET status='NeedsFix',updated_at=now() WHERE id=j.revision_id AND EXISTS(SELECT 1 FROM validation_runs WHERE id=v AND outcome='Failed')
    AND status NOT IN('ConfirmedForTraining','Superseded','Rejected');
   -- Room facts from building_metadata.spaces of an accepted run: Name, LongName (Revit keeps the room name there) and Category.
   IF EXISTS(SELECT 1 FROM validation_runs WHERE id=v AND outcome='Passed') THEN
    INSERT INTO bim_facts(id,revision_id,ifc_global_id,entity_type,property_path,value,source_hash,quality_flags,created_at)
    SELECT gen_random_uuid(),j.revision_id,sp->>'ifcGlobalId','IfcSpace',p.path,p.val,(SELECT sha256_hash FROM source_documents WHERE id=j.source_document_id),
     CASE WHEN p.path='Name' AND NULLIF(btrim(COALESCE(sp->>'name','')),'') IS NULL AND NULLIF(btrim(COALESCE(sp->>'longName','')),'') IS NULL
      THEN '["SPACE_UNNAMED"]'::jsonb ELSE '[]'::jsonb END,now()
    FROM processing_candidate_outputs o CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(o.output->'metadata'->'spaces')='array' THEN o.output->'metadata'->'spaces' ELSE '[]' END) sp
     CROSS JOIN LATERAL (VALUES('Name',sp->'name'),('LongName',sp->'longName'),('Category',sp->'category')) p(path,val)
    WHERE o.attempt_id=a.id AND o.artifact_type='building_metadata' AND jsonb_typeof(sp)='object' AND sp->>'ifcGlobalId' ~ '^[0-9A-Za-z_$]{22}$'
    ON CONFLICT(revision_id,ifc_global_id,property_path) DO UPDATE SET entity_type=EXCLUDED.entity_type,value=EXCLUDED.value,source_hash=EXCLUDED.source_hash,
     quality_flags=EXCLUDED.quality_flags,created_at=EXCLUDED.created_at;
   END IF;
  END IF;
  INSERT INTO audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),b.organization_id,'System','Update','ProcessingJob',j.id,gen_random_uuid(),jsonb_build_object('attemptId',a.id,'outputHash',h),now());
  RETURN jsonb_build_object('code','OK','validationRunId',v,'outputHash',h);
 ELSIF p_action='Fail' THEN
  UPDATE processing_job_attempts SET status='Failed',finished_at=clock_timestamp(),error_message=left(p_input->>'reason',1000) WHERE id=a.id;
  UPDATE processing_jobs SET status='Failed',finished_at=clock_timestamp(),error_message=left(p_input->>'reason',1000) WHERE id=j.id;
  INSERT INTO audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at) VALUES(gen_random_uuid(),b.organization_id,'System','Update','ProcessingJob',j.id,gen_random_uuid(),jsonb_build_object('attemptId',a.id,'status','Failed'),now());
  RETURN jsonb_build_object('code','OK');
 END IF;
 RETURN jsonb_build_object('code','WORKER_ACTION_INVALID','status',400);
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON bim_facts FROM %I',r);END IF;END LOOP;
 -- The worker gate writes facts; the API reads them through IfcReadStore with the runtime login, scoped to the revision tenant.
 GRANT SELECT,INSERT,UPDATE ON bim_facts TO fet3d_ifc_upload_owner;
 IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='bim_facts' AND policyname='bim_facts_gate_owner') THEN
  CREATE POLICY bim_facts_gate_owner ON bim_facts TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);END IF;
 FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT SELECT ON bim_facts TO %I',r);
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='bim_facts' AND policyname='bim_facts_read_'||r) THEN
   EXECUTE format('CREATE POLICY %I ON bim_facts FOR SELECT TO %I USING(true)','bim_facts_read_'||r,r);END IF;
 END IF;END LOOP;
 GRANT SELECT ON source_documents TO fet3d_ifc_upload_owner;
 ALTER FUNCTION processing_worker_gate_before_streams(text,uuid,jsonb) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION processing_worker_gate_before_streams(text,uuid,jsonb) FROM PUBLIC;
 SELECT * INTO s FROM ifc_qa_contract_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
