-- Geometry pull mode: the IFC/Geometry worker runs on the same outbound-only Windows VM as the Unity package worker and pulls
-- Geometry jobs exactly like package jobs (BE#110 pattern), so the HTTP push dispatcher is not needed for Geometry.
-- 1. processing_geometry_lease_gate: recovers expired attempts, then claims the oldest deliverable Geometry job through the existing
--    worker gate (same attempt, receipt, consumption and fencing rules) and marks its outbox event Published in the same
--    transaction. The new attempt is tagged lease_owner='geometry-pull-worker' and gets the lease length the API passes (60-900 s).
--    A delivery that already has a claim (replayed: an earlier push delivery whose ACK was lost) is marked Published and skipped,
--    so two workers never share one attempt.
-- 2. processing_worker_gate (wrapper): Renew of a pulled Geometry attempt extends by geometryLeaseSeconds (60-900, set by the API);
--    push Geometry attempts keep 60 seconds. Package rules are unchanged (body of PackageBuildPull.sql).
-- 3. processing_worker_context adds, for Geometry attempts, jobId/kind/revisionId/outputPrefix, whether the attempt was pulled, the
--    verified IFC source (the API signs a short GET URL for it) and the registered candidate outputs (the API checks storage
--    before Complete of a pulled attempt).
-- 4. processing_dispatch_gate('Claim', key): ExcludePackages (unchanged), ExcludeGeometry and ExcludePackagesAndGeometry keep the
--    HTTP dispatcher away from the job kinds that are pulled. Recover/Ack/Fail are unchanged.
-- Additive: no table, column or data change; functions are redefined with the same signatures, owner and grants.
CREATE TEMP TABLE geometry_pull_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO geometry_pull_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION processing_dispatch_gate(p_action text,p_key text DEFAULT NULL,p_token uuid DEFAULT NULL,p_receipt uuid DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE e integration_outbox_events;token uuid:=gen_random_uuid();a processing_job_attempts;j processing_jobs;k text;
BEGIN
 IF p_action='Claim' THEN
  SELECT * INTO e FROM integration_outbox_events WHERE aggregate_type='ProcessingJob' AND event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND available_at<=clock_timestamp() AND (status='Pending' OR status='Leased' AND lease_until<=clock_timestamp())
   -- The HTTP dispatcher leaves pulled job kinds to their pull lease: ExcludePackages (BE#110), ExcludeGeometry, or both.
   AND NOT EXISTS(SELECT 1 FROM processing_jobs pj WHERE pj.id=integration_outbox_events.aggregate_id
    AND (p_key IN('ExcludePackages','ExcludePackagesAndGeometry') AND pj.kind::text IN('PlaytestPackage','ReleasePackage')
     OR p_key IN('ExcludeGeometry','ExcludePackagesAndGeometry') AND pj.kind::text='Geometry'))
   ORDER BY available_at,idempotency_key LIMIT 1 FOR UPDATE SKIP LOCKED;
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

CREATE OR REPLACE FUNCTION processing_worker_context(p_attempt uuid,p_token uuid) RETURNS jsonb
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('inputSnapshot',j.input_snapshot,'scenarioSnapshot',v.state_snapshot)
  ||CASE WHEN j.kind IN('PlaytestPackage','ReleasePackage') THEN jsonb_build_object('jobId',j.id,'kind',j.kind,'buildTarget',j.input_snapshot->>'buildTarget',
    'outputPrefix','worker-output/'||b.organization_id||'/'||j.id||'/'||a.id||'/','geometryAttemptId',g.id,
    'geometryInputs',COALESCE((SELECT jsonb_agg(jsonb_build_object('artifactType',x.artifact_type,'artifactId',x.id,'objectKey',x.object_key,'sha256',x.sha256_hash,
      'sizeBytes',x.size_bytes,'schemaVersion',x.schema_version) ORDER BY x.artifact_type,x.id) FROM revision_artifacts x WHERE x.attempt_id=g.id AND x.revision_id=j.revision_id),'[]'::jsonb),
    'packageManifest',(SELECT o.output FROM processing_candidate_outputs o WHERE o.attempt_id=a.id AND o.artifact_type='manifest'))
   -- Geometry: the verified IFC source of the job (same row the worker gate requires on Claim) and the registered outputs.
   WHEN j.kind='Geometry' THEN jsonb_build_object('jobId',j.id,'kind',j.kind,'revisionId',j.revision_id,
    'outputPrefix','worker-output/'||b.organization_id||'/'||j.id||'/'||a.id||'/','pulled',a.lease_owner='geometry-pull-worker',
    'sourceInput',(SELECT jsonb_build_object('artifactType','ifc_source','artifactId',s.id,'objectKey',s.storage_url,'sha256',lower(s.sha256_hash),'sizeBytes',s.file_size_bytes)
      FROM source_documents s WHERE s.id=j.source_document_id AND s.revision_id=j.revision_id AND s.upload_verified_at IS NOT NULL AND s.source_etag IS NOT NULL),
    'geometryOutputs',COALESCE((SELECT jsonb_agg(jsonb_build_object('artifactType',o.artifact_type,'objectKey',o.output->>'objectKey','sha256Hash',o.output->>'sha256Hash',
      'sizeBytes',(o.output->>'sizeBytes')::bigint) ORDER BY o.artifact_type) FROM processing_candidate_outputs o WHERE o.attempt_id=a.id),'[]'::jsonb))
   ELSE '{}'::jsonb END
 FROM processing_job_attempts a JOIN processing_jobs j ON j.id=a.processing_job_id AND j.current_attempt_id=a.id
  JOIN revisions r ON r.id=j.revision_id JOIN buildings b ON b.id=r.building_id
  LEFT JOIN scenario_versions v ON v.id=j.scenario_version_id
  LEFT JOIN LATERAL (SELECT accepted_geometry_attempt(j.revision_id) AS id) g ON true
 WHERE a.id=p_attempt AND a.lease_token=p_token AND (a.status='Succeeded' OR a.status='Running' AND a.lease_until>clock_timestamp())
$$;

CREATE OR REPLACE FUNCTION processing_worker_gate(p_action text,p_job uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE result jsonb;c integration_event_consumptions;k text;h text;r text;kind text;secs integer;stamp timestamptz;o jsonb;m processing_candidate_outputs;u processing_candidate_outputs;
BEGIN
 SELECT j.kind INTO kind FROM processing_jobs j WHERE j.id=p_job;
 IF kind IN('PlaytestPackage','ReleasePackage') AND p_action='Output' THEN
  o:=p_input->'output';
  -- fet3d.package/1: the manifest sits at the root of the attempt prefix and lists every file with its SHA-256 and size.
  IF o->>'artifactType'='manifest' AND o->>'schemaVersion'='fet3d.package/1' AND (o->>'objectKey' !~ ('/'||p_job||'/'||COALESCE(p_input->>'attemptId','')||'/manifest\.json$')
   OR jsonb_typeof(o->'metadata'->'files') IS DISTINCT FROM 'array' OR jsonb_typeof(o->'metadata'->'floors') IS DISTINCT FROM 'array') THEN
   RETURN jsonb_build_object('code','PACKAGE_MANIFEST_INVALID','status',422);
  END IF;
 ELSIF kind IN('PlaytestPackage','ReleasePackage') AND p_action='Complete' THEN
  SELECT * INTO m FROM processing_candidate_outputs WHERE attempt_id=(p_input->>'attemptId')::uuid AND artifact_type='manifest';
  SELECT * INTO u FROM processing_candidate_outputs WHERE attempt_id=(p_input->>'attemptId')::uuid AND artifact_type='unity_package';
  IF m.output->>'schemaVersion'='fet3d.package/1' AND u.attempt_id IS NOT NULL AND (u.output->>'objectKey' IS DISTINCT FROM m.output->>'objectKey'
   OR u.output->>'sha256Hash' IS DISTINCT FROM m.output->>'sha256Hash' OR u.output->>'sizeBytes' IS DISTINCT FROM m.output->>'sizeBytes') THEN
   RETURN jsonb_build_object('code','PACKAGE_MANIFEST_INVALID','status',422);
  END IF;
 END IF;
 result:=processing_worker_gate_before_streams(p_action,p_job,p_input-'leaseSeconds'-'geometryLeaseSeconds');
 IF p_action='Claim' AND result->>'code'='OK' THEN
  k:=p_input->>'eventKey';h:=p_input->>'payloadHash';r:=result->>'receiptId';
  INSERT INTO integration_event_consumptions(consumer_name,event_key,payload_hash,result_reference) VALUES('processing-bridge-v1',k,h,r) ON CONFLICT(consumer_name,event_key) DO NOTHING;
  SELECT * INTO c FROM integration_event_consumptions WHERE consumer_name='processing-bridge-v1' AND event_key=k;
  IF c.payload_hash IS DISTINCT FROM h OR c.result_reference IS DISTINCT FROM r THEN RAISE EXCEPTION 'Processing handoff receipt conflict';END IF;
 END IF;
 -- Package jobs: the lease length comes from the API configuration, never from the worker (the controller overwrites leaseSeconds).
 IF kind IN('PlaytestPackage','ReleasePackage') AND result->>'code'='OK' AND jsonb_typeof(p_input->'leaseSeconds')='number'
  AND (p_action='Renew' OR p_action='Claim' AND NOT COALESCE((result->>'replayed')::boolean,false)) THEN
  secs:=least(900,greatest(60,round((p_input->>'leaseSeconds')::numeric)::integer));
  UPDATE processing_job_attempts SET lease_until=clock_timestamp()+make_interval(secs=>secs)
   WHERE id=COALESCE((result->>'attemptId')::uuid,(p_input->>'attemptId')::uuid) AND status='Running' RETURNING lease_until INTO stamp;
  IF stamp IS NOT NULL THEN result:=result||jsonb_build_object('leaseUntil',stamp);END IF;
 -- Geometry: only an attempt obtained through processing_geometry_lease_gate renews with the configured long lease; push stays 60 s.
 ELSIF kind='Geometry' AND p_action='Renew' AND result->>'code'='OK' AND jsonb_typeof(p_input->'geometryLeaseSeconds')='number' THEN
  secs:=least(900,greatest(60,round((p_input->>'geometryLeaseSeconds')::numeric)::integer));
  UPDATE processing_job_attempts SET lease_until=clock_timestamp()+make_interval(secs=>secs)
   WHERE id=(p_input->>'attemptId')::uuid AND status='Running' AND lease_owner='geometry-pull-worker' RETURNING lease_until INTO stamp;
  IF stamp IS NOT NULL THEN result:=result||jsonb_build_object('leaseUntil',stamp);END IF;
 END IF;
 RETURN result;
END $$;

CREATE FUNCTION processing_geometry_lease_gate(p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE cand record;e integration_outbox_events;result jsonb;i integer;secs integer;stamp timestamptz;
BEGIN
 IF NULLIF(p_input->>'toolchainVersion','') IS NULL OR jsonb_typeof(p_input->'leaseSeconds') IS DISTINCT FROM 'number' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 secs:=least(900,greatest(60,round((p_input->>'leaseSeconds')::numeric)::integer));
 -- A worker that died keeps its attempt Running until the lease passes; recover a few here so its job is offered again.
 FOR i IN 1..5 LOOP EXIT WHEN processing_dispatch_gate('Recover')->>'code'<>'OK';END LOOP;
 FOR cand IN SELECT o.idempotency_key,j.id AS job_id,j.input_hash,r.building_id FROM integration_outbox_events o
   JOIN processing_jobs j ON j.id=o.aggregate_id JOIN revisions r ON r.id=j.revision_id
  WHERE o.aggregate_type='ProcessingJob' AND o.event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND o.schema_version='1'
   AND o.available_at<=clock_timestamp() AND (o.status='Pending' OR o.status='Leased' AND o.lease_until<=clock_timestamp())
   AND j.kind='Geometry' AND j.status IN('Queued','Running')
  ORDER BY o.available_at,o.idempotency_key LIMIT 20
 LOOP
  -- Same lock order as the worker gate: identity, Building, then the event row.
  PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||cand.building_id,0));
  SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=cand.idempotency_key AND available_at<=clock_timestamp()
   AND (status='Pending' OR status='Leased' AND lease_until<=clock_timestamp()) FOR UPDATE SKIP LOCKED;
  CONTINUE WHEN e.idempotency_key IS NULL;
  result:=processing_worker_gate('Claim',cand.job_id,jsonb_build_object('eventKey',e.idempotency_key,'payloadHash',e.payload_hash,'inputHash',cand.input_hash,
   'toolchainVersion',p_input->>'toolchainVersion'));
  IF result->>'code'='OK' THEN
   UPDATE integration_outbox_events SET status='Published',lease_owner=NULL,lease_token=NULL,lease_until=NULL,attempts=attempts+1,published_at=clock_timestamp(),
    published_lease_token=(result->>'leaseToken')::uuid,last_error=NULL WHERE idempotency_key=e.idempotency_key;
   -- The delivery was already claimed (a push delivery whose ACK was lost): that worker holds the attempt; it is not handed out twice.
   CONTINUE WHEN COALESCE((result->>'replayed')::boolean,false);
   UPDATE processing_job_attempts SET lease_owner='geometry-pull-worker',lease_until=clock_timestamp()+make_interval(secs=>secs)
    WHERE id=(result->>'attemptId')::uuid AND status='Running' RETURNING lease_until INTO stamp;
   IF stamp IS NOT NULL THEN result:=result||jsonb_build_object('leaseUntil',stamp);END IF;
   RETURN result||jsonb_build_object('jobId',cand.job_id,'eventKey',e.idempotency_key);
  ELSIF result->>'code' IN('JOB_BUSY','JOB_SCOPE_UNAVAILABLE') THEN
   UPDATE integration_outbox_events SET status='Pending',lease_owner=NULL,lease_token=NULL,lease_until=NULL,attempts=attempts+1,
    available_at=clock_timestamp()+make_interval(secs=>least(1800,30*(2^least(attempts,6))::integer)),last_error=result->>'code' WHERE idempotency_key=e.idempotency_key;
  ELSE
   -- Stale or conflicting deliveries never become claimable again; a new attempt always comes with a new Recover/retry event.
   UPDATE integration_outbox_events SET status='Failed',lease_owner=NULL,lease_token=NULL,lease_until=NULL,attempts=attempts+1,last_error=result->>'code' WHERE idempotency_key=e.idempotency_key;
  END IF;
 END LOOP;
 RETURN jsonb_build_object('code','EMPTY');
END $$;

DO $grants$ DECLARE r text;s record;sig text;BEGIN
 FOREACH sig IN ARRAY ARRAY['processing_dispatch_gate(text,text,uuid,uuid)','processing_worker_context(uuid,uuid)','processing_worker_gate(text,uuid,jsonb)',
  'processing_geometry_lease_gate(jsonb)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 GRANT EXECUTE ON FUNCTION processing_geometry_lease_gate(jsonb),processing_worker_gate(text,uuid,jsonb),processing_worker_context(uuid,uuid) TO fet3d_processing_executor;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION processing_dispatch_gate(text,text,uuid,uuid) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM geometry_pull_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
