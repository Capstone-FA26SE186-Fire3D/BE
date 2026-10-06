-- Selected BE transport: transactional PostgreSQL outbox -> HTTP worker. No Redis.
-- No provenance is fabricated for legacy jobs/artifacts.
DO $$ BEGIN IF EXISTS(SELECT 1 FROM processing_jobs WHERE input_hash !~ '^[0-9a-fA-F]{64}$') THEN
 RAISE EXCEPTION 'Processing migration preflight: legacy input hash requires operator review'; END IF; END $$;
CREATE TABLE processing_job_attempts (
 id uuid PRIMARY KEY, processing_job_id uuid NOT NULL REFERENCES processing_jobs(id), attempt_number integer NOT NULL CHECK(attempt_number>0),
 input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[0-9a-f]{64}$'), toolchain_version varchar(100) NOT NULL,
 lease_owner varchar(100) NOT NULL, lease_token uuid NOT NULL UNIQUE, lease_until timestamptz NOT NULL,
 status text NOT NULL CHECK(status IN('Running','Succeeded','Failed','Expired')), output_hash varchar(64),
 created_at timestamptz NOT NULL DEFAULT now(),started_at timestamptz NOT NULL DEFAULT now(),finished_at timestamptz,error_message text,
 UNIQUE(processing_job_id,attempt_number),UNIQUE(id,processing_job_id),CHECK(output_hash IS NULL OR output_hash ~ '^[0-9a-f]{64}$')
);
ALTER TABLE processing_jobs ADD COLUMN current_attempt_id uuid REFERENCES processing_job_attempts(id);
DO $$ BEGIN IF EXISTS(SELECT 1 FROM processing_jobs GROUP BY revision_id,kind,input_hash,scenario_version_id HAVING count(*)>1) THEN
 RAISE EXCEPTION 'Processing migration preflight: duplicate logical inputs require operator review'; END IF; END $$;
CREATE UNIQUE INDEX processing_verified_input_key ON processing_jobs(revision_id,kind,input_hash,COALESCE(scenario_version_id,'00000000-0000-0000-0000-000000000000'::uuid));
CREATE TABLE processing_command_receipts (
 actor_id uuid NOT NULL REFERENCES users(id),operation text NOT NULL,idempotency_key text NOT NULL,input_hash varchar(64) NOT NULL,
 job_id uuid NOT NULL REFERENCES processing_jobs(id),created_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(actor_id,operation,idempotency_key)
);
CREATE TABLE processing_delivery_receipts (
 event_key text PRIMARY KEY REFERENCES integration_outbox_events(idempotency_key),payload_hash varchar(64) NOT NULL,
 attempt_id uuid NOT NULL REFERENCES processing_job_attempts(id),receipt_id uuid NOT NULL UNIQUE,created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE processing_candidate_outputs (
 attempt_id uuid NOT NULL REFERENCES processing_job_attempts(id),artifact_type text NOT NULL,input_hash varchar(64) NOT NULL,
 artifact_id uuid NOT NULL UNIQUE,output jsonb NOT NULL,PRIMARY KEY(attempt_id,artifact_type)
);
ALTER TABLE revision_artifacts ADD COLUMN attempt_id uuid REFERENCES processing_job_attempts(id),
 ADD COLUMN is_runtime_ready boolean NOT NULL DEFAULT false,ADD COLUMN storage_key text GENERATED ALWAYS AS(object_key) STORED;
ALTER TABLE validation_runs ADD COLUMN processing_attempt_id uuid REFERENCES processing_job_attempts(id),
 ADD COLUMN processing_job_id uuid GENERATED ALWAYS AS(job_id) STORED,
 ADD COLUMN artifact_id uuid GENERATED ALWAYS AS(candidate_artifact_id) STORED,
 ADD COLUMN scope text GENERATED ALWAYS AS(kind) STORED,ADD COLUMN status text GENERATED ALWAYS AS(outcome) STORED,
 ADD COLUMN summary jsonb GENERATED ALWAYS AS(report) STORED,ADD COLUMN started_at timestamptz,ADD COLUMN finished_at timestamptz;
CREATE VIEW validation_issues AS SELECT i.id,v.revision_id,i.validation_run_id,v.candidate_artifact_id AS artifact_id,
 i.code AS issue_code,i.severity,'Open'::text AS status,i.message,i.details AS evidence,i.created_at
 FROM revision_issues i JOIN validation_runs v ON v.id=i.validation_run_id;
CREATE INDEX processing_attempt_expiry ON processing_job_attempts(status,lease_until);

-- Reuse the restricted NOLOGIN owner; restore temporary grants/membership before commit.
CREATE TEMP TABLE processing_permission_state(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;hc boolean;t text; BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 hc:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'); INSERT INTO processing_permission_state VALUES(os,oi,c,hc);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user); END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 FOREACH t IN ARRAY ARRAY['processing_jobs','processing_job_attempts','processing_command_receipts','processing_delivery_receipts','processing_candidate_outputs','revision_artifacts','validation_runs','revision_issues','integration_outbox_events','scenario_versions'] LOOP
  EXECUTE format('GRANT SELECT,INSERT,UPDATE ON %I TO fet3d_ifc_upload_owner',t);
  EXECUTE format('CREATE POLICY processing_owner_access ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['processing_job_attempts','processing_command_receipts','processing_delivery_receipts','processing_candidate_outputs'] LOOP
  EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t); EXECUTE format('REVOKE ALL ON %I FROM PUBLIC',t);
 END LOOP;
 REVOKE INSERT,UPDATE ON scenario_versions FROM fet3d_ifc_upload_owner;
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_processing_executor') THEN CREATE ROLE fet3d_processing_executor NOLOGIN NOSUPERUSER NOBYPASSRLS; END IF;
 GRANT USAGE ON SCHEMA public TO fet3d_processing_executor;
 GRANT EXECUTE ON FUNCTION enqueue_integration_outbox_event(text,text,uuid,text,text,jsonb),enqueue_integration_outbox_event_internal(text,text,uuid,text,text,jsonb,boolean,boolean) TO fet3d_ifc_upload_owner;
END $$;

CREATE FUNCTION request_revision_processing(p_actor uuid,p_revision uuid,p_key text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE u users;r revisions;b buildings;s source_documents;receipt processing_command_receipts;j uuid;h text;
BEGIN
 IF p_key IS NULL OR length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400); END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO u FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF u.id IS NULL THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401); END IF;
 IF u.role NOT IN('OrganizationUser','PlatformAdmin') THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403); END IF;
 SELECT * INTO r FROM revisions WHERE id=p_revision;
 SELECT * INTO b FROM buildings WHERE id=r.building_id AND is_active AND deleted_at IS NULL;
 IF b.id IS NULL OR (u.role='OrganizationUser' AND u.organization_id IS DISTINCT FROM b.organization_id)
 OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=b.organization_id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('processing-command:'||p_actor||':'||p_key,0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||b.id,0));
 IF NOT EXISTS(SELECT 1 FROM buildings WHERE id=b.id AND is_active AND deleted_at IS NULL) THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404); END IF;
 SELECT * INTO s FROM source_documents WHERE revision_id=r.id AND upload_verified_at IS NOT NULL AND source_etag IS NOT NULL;
 IF s.id IS NULL THEN RETURN jsonb_build_object('code','IFC_SOURCE_NOT_VERIFIED','status',422); END IF;
 h:=fet3d_jsonb_payload_hash(jsonb_build_object('revisionId',r.id,'sourceId',s.id,'sha256',s.sha256_hash));
 SELECT * INTO receipt FROM processing_command_receipts WHERE actor_id=p_actor AND operation='Process' AND idempotency_key=p_key;
 IF receipt.job_id IS NOT NULL THEN
  IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409); END IF;
  RETURN jsonb_build_object('code','OK','jobId',receipt.job_id);
 END IF;
 SELECT id INTO j FROM processing_jobs WHERE revision_id=r.id AND kind='Geometry' AND input_hash=lower(s.sha256_hash) AND scenario_version_id IS NULL;
 IF j IS NULL THEN
  j:=gen_random_uuid();
  INSERT INTO processing_jobs(id,revision_id,source_document_id,kind,job_key,status,input_hash,created_at) VALUES(j,r.id,s.id,'Geometry',gen_random_uuid(),'Queued',lower(s.sha256_hash),now());
  PERFORM enqueue_integration_outbox_event('ifc-process:'||replace(j::text,'-',''),'ProcessingJob',j,'ProcessingJobRequested','1',jsonb_build_object('job_id',j,'revision_id',r.id,'source_key',s.storage_url,'input_hash',lower(s.sha256_hash)));
  INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,created_at) VALUES(gen_random_uuid(),p_actor,b.organization_id,'User','Create','ProcessingJob',j,gen_random_uuid(),now());
 END IF;
 INSERT INTO processing_command_receipts(actor_id,operation,idempotency_key,input_hash,job_id) VALUES(p_actor,'Process',p_key,h,j);
 RETURN jsonb_build_object('code','OK','jobId',j);
END $$;

CREATE FUNCTION processing_worker_gate(p_action text,p_job uuid,p_input jsonb) RETURNS jsonb
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
   IF primary_art IS NULL OR outrow.artifact_type='unity_package' THEN primary_art:=outrow.artifact_id; summary:=outrow.output; END IF;
  END LOOP;
  INSERT INTO validation_runs(id,revision_id,job_id,kind,scenario_version_id,candidate_artifact_id,outcome,scenario_hash,validator_version,report,created_at,processing_attempt_id,started_at,finished_at)
   VALUES(v,j.revision_id,j.id,j.kind,j.scenario_version_id,primary_art,CASE WHEN EXISTS(SELECT 1 FROM processing_candidate_outputs WHERE attempt_id=a.id AND (output->>'outcome'='Failed' OR EXISTS(SELECT 1 FROM jsonb_array_elements(output->'issues') i WHERE i->>'severity' IN('Error','Critical')))) THEN 'Failed' ELSE 'Passed' END,
    CASE WHEN j.scenario_version_id IS NULL THEN NULL ELSE (SELECT scenario_hash FROM scenario_versions WHERE id=j.scenario_version_id) END,summary->>'validatorVersion',jsonb_build_object('outputHash',h,'toolchainVersion',a.toolchain_version),now(),a.id,a.started_at,clock_timestamp());
  FOR issue IN SELECT value FROM processing_candidate_outputs o CROSS JOIN LATERAL jsonb_array_elements(o.output->'issues') WHERE o.attempt_id=a.id LOOP
   INSERT INTO revision_issues(id,validation_run_id,severity,code,message,details,created_at) VALUES(gen_random_uuid(),v,issue->>'severity',issue->>'code',issue->>'message',COALESCE(issue->'details','{}'::jsonb),now());
  END LOOP;
  UPDATE processing_job_attempts SET status='Succeeded',output_hash=h,finished_at=clock_timestamp() WHERE id=a.id;
  UPDATE processing_jobs SET status='Succeeded',finished_at=clock_timestamp(),error_message=NULL WHERE id=j.id;
  IF j.kind='Geometry' THEN UPDATE revisions SET status='ReadyForScenario',updated_at=now() WHERE id=j.revision_id AND EXISTS(SELECT 1 FROM validation_runs WHERE id=v AND outcome='Passed'); END IF;
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

CREATE FUNCTION processing_dispatch_gate(p_action text,p_key text DEFAULT NULL,p_token uuid DEFAULT NULL,p_receipt uuid DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE e integration_outbox_events;token uuid:=gen_random_uuid();a processing_job_attempts;j processing_jobs;k text;
BEGIN
 IF p_action='Claim' THEN
  SELECT * INTO e FROM integration_outbox_events WHERE aggregate_type='ProcessingJob' AND event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND available_at<=clock_timestamp() AND (status='Pending' OR status='Leased' AND lease_until<=clock_timestamp()) ORDER BY available_at,idempotency_key LIMIT 1 FOR UPDATE SKIP LOCKED;
  IF e.idempotency_key IS NULL THEN RETURN jsonb_build_object('code','EMPTY'); END IF;
  UPDATE integration_outbox_events SET status='Leased',lease_owner='http-dispatcher',lease_token=token,lease_until=clock_timestamp()+interval '60 seconds',attempts=attempts+1 WHERE idempotency_key=e.idempotency_key;
  RETURN jsonb_build_object('code','OK','eventKey',e.idempotency_key,'payloadHash',e.payload_hash,'payload',e.payload,'leaseToken',token,'attempt',e.attempts+1);
 ELSIF p_action IN('Ack','Fail') THEN
  SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=p_key FOR UPDATE;
  IF e.status<>'Leased' OR e.lease_token IS DISTINCT FROM p_token OR e.lease_until<=clock_timestamp() THEN RETURN jsonb_build_object('code','LEASE_STALE','status',409); END IF;
  IF p_action='Ack' THEN
   IF NOT EXISTS(SELECT 1 FROM processing_delivery_receipts WHERE event_key=p_key AND payload_hash=e.payload_hash AND receipt_id=p_receipt) THEN RETURN jsonb_build_object('code','RECEIPT_REQUIRED','status',409); END IF;
   UPDATE integration_outbox_events SET status='Published',lease_owner=NULL,lease_token=NULL,lease_until=NULL,published_at=clock_timestamp(),published_lease_token=p_token WHERE idempotency_key=p_key;
  ELSE UPDATE integration_outbox_events SET status=CASE WHEN attempts>=10 THEN 'Failed' ELSE 'Pending' END,lease_owner=NULL,lease_token=NULL,lease_until=NULL,available_at=clock_timestamp()+make_interval(secs=>least(1800,30*(2^least(attempts,6))::integer)),last_error='HTTP_DELIVERY_UNAVAILABLE' WHERE idempotency_key=p_key; END IF;
  RETURN jsonb_build_object('code','OK');
 ELSIF p_action='Recover' THEN
  -- Resource/lifecycle -> event -> job locking. Never silently retry Failed/Cancelled/Succeeded.
  SELECT * INTO a FROM processing_job_attempts WHERE status='Running' AND lease_until<=clock_timestamp() ORDER BY lease_until,id LIMIT 1;
  IF a.id IS NULL THEN RETURN jsonb_build_object('code','EMPTY'); END IF;
  PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
  SELECT * INTO j FROM processing_jobs WHERE id=a.processing_job_id;
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||(SELECT building_id FROM revisions WHERE id=j.revision_id),0));
  k:='ifc-recover:'||a.id;
  PERFORM pg_advisory_xact_lock(hashtextextended(k,0));
  SELECT * INTO j FROM processing_jobs WHERE id=a.processing_job_id FOR UPDATE;
  SELECT * INTO a FROM processing_job_attempts WHERE id=a.id FOR UPDATE;
  IF j.status<>'Running' OR j.current_attempt_id IS DISTINCT FROM a.id OR a.status<>'Running' OR a.lease_until>clock_timestamp() THEN RETURN jsonb_build_object('code','EMPTY'); END IF;
  UPDATE processing_job_attempts SET status='Expired',finished_at=clock_timestamp() WHERE id=a.id;
  UPDATE processing_jobs SET status='Queued' WHERE id=j.id;
  PERFORM enqueue_integration_outbox_event_internal(k,'ProcessingJob',j.id,'ProcessingJobRequeue','1',jsonb_build_object('job_id',j.id,'input_hash',j.input_hash,'expired_attempt_id',a.id,'reason','AttemptLeaseExpired'),false,true);
  RETURN jsonb_build_object('code','OK');
 END IF;
 RETURN jsonb_build_object('code','DISPATCH_ACTION_INVALID','status',400);
END $$;

CREATE OR REPLACE FUNCTION requeue_processing_job(p_job uuid,p_key text,p_reason text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE j processing_jobs;e integration_outbox_events;payload jsonb;
BEGIN
 PERFORM pg_advisory_xact_lock(hashtextextended(p_key,0));
 SELECT * INTO j FROM processing_jobs WHERE id=p_job FOR UPDATE;
 SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=p_key;
 payload:=jsonb_build_object('job_id',p_job,'reason',p_reason,'input_hash',j.input_hash);
 IF e.idempotency_key IS NOT NULL THEN
  IF e.aggregate_id IS DISTINCT FROM p_job OR e.event_type<>'ProcessingJobRequeue' OR e.payload IS DISTINCT FROM payload THEN RAISE EXCEPTION 'requeue idempotency key conflicts with a different envelope'; END IF;
  RETURN 'AlreadyRequeued';
 END IF;
 IF j.id IS NULL OR j.status<>'Failed' THEN RETURN 'NotClaimable'; END IF;
 UPDATE processing_jobs SET status='Queued',finished_at=NULL,error_message=NULL WHERE id=j.id;
 PERFORM enqueue_integration_outbox_event_internal(p_key,'ProcessingJob',j.id,'ProcessingJobRequeue','1',payload,false,true);
 RETURN 'Requeued';
END $$;

DO $$ DECLARE s text;r text;t text;state record; BEGIN
 FOREACH s IN ARRAY ARRAY['request_revision_processing(uuid,uuid,text)','processing_worker_gate(text,uuid,jsonb)','processing_dispatch_gate(text,text,uuid,uuid)','requeue_processing_job(uuid,text,text)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',s); EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',s);
 END LOOP;
 GRANT EXECUTE ON FUNCTION processing_worker_gate(text,uuid,jsonb) TO fet3d_processing_executor;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION request_revision_processing(uuid,uuid,text),processing_dispatch_gate(text,text,uuid,uuid),requeue_processing_job(uuid,text,text) TO %I',r);
  EXECUTE format('GRANT SELECT ON processing_job_attempts,validation_issues TO %I',r);
  EXECUTE format('CREATE POLICY processing_attempt_read_%I ON processing_job_attempts FOR SELECT TO %I USING(true)',r,r);
  FOREACH t IN ARRAY ARRAY['processing_jobs','revision_artifacts','validation_runs','revision_issues'] LOOP EXECUTE format('REVOKE INSERT,UPDATE,DELETE ON %I FROM %I',t,r); END LOOP;
 END IF; END LOOP;
 SELECT * INTO state FROM processing_permission_state;
 IF NOT state.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner; END IF;
 IF state.changed THEN
  IF state.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s, INHERIT %s',current_user,CASE WHEN state.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN state.original_inherit THEN 'TRUE' ELSE 'FALSE' END); END IF;
 END IF;
END $$;
DROP TABLE processing_permission_state;
