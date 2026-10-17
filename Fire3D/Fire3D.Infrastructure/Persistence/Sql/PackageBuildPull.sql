-- BE#110: Unity package build worker on an outbound-only Azure VM (pull mode).
-- 1. processing_package_lease_gate: the worker asks for the oldest deliverable PlaytestPackage/ReleasePackage job and claims it in
--    one transaction through the existing worker gate (same attempt, receipt, consumption and fencing rules). The outbox event is
--    marked Published together with the claim, so there is no separate ACK. Expired attempts are recovered first, so a lost worker's
--    job comes back without the push dispatcher. processing_dispatch_gate('Claim','ExcludePackages') keeps the HTTP dispatcher away
--    from package jobs while pull is on (Redis transport is not combined with pull; the API refuses that configuration).
-- 2. processing_worker_gate (wrapper): package jobs get the lease length the API passes (leaseSeconds, 60-900) on Claim and Renew,
--    because a Unity bake takes tens of minutes; Geometry keeps 60 seconds. For a fet3d.package/1 manifest the manifest must sit at
--    <outputPrefix>manifest.json and the unity_package output must name the same object, hash and size: the package is the manifest
--    plus the files it lists with their SHA-256, so every existing reader of the package/manifest pins keeps working.
-- 3. processing_worker_context adds, for package jobs, the output prefix, the artifacts of the accepted Geometry attempt of the
--    revision (the API signs short GET URLs for them) and the registered manifest candidate (the API verifies storage before Complete).
-- 4. runtime_package_files: the pinned fet3d.package/1 manifest and file list of a started learner session or playtest, for the
--    caller's launching login only; the API returns short signed GET URLs.
CREATE TEMP TABLE package_pull_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO package_pull_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION processing_dispatch_gate(p_action text,p_key text DEFAULT NULL,p_token uuid DEFAULT NULL,p_receipt uuid DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE e integration_outbox_events;token uuid:=gen_random_uuid();a processing_job_attempts;j processing_jobs;k text;
BEGIN
 IF p_action='Claim' THEN
  SELECT * INTO e FROM integration_outbox_events WHERE aggregate_type='ProcessingJob' AND event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND available_at<=clock_timestamp() AND (status='Pending' OR status='Leased' AND lease_until<=clock_timestamp())
   -- BE#110: with package pull on, the HTTP dispatcher passes ExcludePackages and leaves Unity package jobs to the pull lease.
   AND (p_key IS DISTINCT FROM 'ExcludePackages' OR NOT EXISTS(SELECT 1 FROM processing_jobs pj WHERE pj.id=integration_outbox_events.aggregate_id AND pj.kind IN('PlaytestPackage','ReleasePackage')))
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

-- The accepted Geometry attempt of a revision: current Succeeded attempt with a Passed validation run and no Error/Critical issue
-- (same rule as revision_equipment_gate and the editor preview).
CREATE FUNCTION accepted_geometry_attempt(p_revision uuid) RETURNS uuid LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT pa.id FROM processing_jobs gj JOIN processing_job_attempts pa ON pa.id=gj.current_attempt_id AND pa.processing_job_id=gj.id
  WHERE gj.revision_id=p_revision AND gj.kind='Geometry' AND gj.status='Succeeded' AND pa.status='Succeeded'
   AND EXISTS(SELECT 1 FROM validation_runs vr WHERE vr.job_id=gj.id AND vr.processing_attempt_id=pa.id AND vr.revision_id=p_revision
    AND vr.kind='Geometry' AND vr.outcome='Passed' AND NOT EXISTS(SELECT 1 FROM revision_issues i WHERE i.validation_run_id=vr.id AND i.severity IN('Error','Critical')))
  ORDER BY pa.finished_at DESC NULLS LAST,pa.id LIMIT 1
$$;

CREATE OR REPLACE FUNCTION processing_worker_context(p_attempt uuid,p_token uuid) RETURNS jsonb
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
 SELECT jsonb_build_object('inputSnapshot',j.input_snapshot,'scenarioSnapshot',v.state_snapshot)
  ||CASE WHEN j.kind IN('PlaytestPackage','ReleasePackage') THEN jsonb_build_object('jobId',j.id,'kind',j.kind,'buildTarget',j.input_snapshot->>'buildTarget',
    'outputPrefix','worker-output/'||b.organization_id||'/'||j.id||'/'||a.id||'/','geometryAttemptId',g.id,
    'geometryInputs',COALESCE((SELECT jsonb_agg(jsonb_build_object('artifactType',x.artifact_type,'artifactId',x.id,'objectKey',x.object_key,'sha256',x.sha256_hash,
      'sizeBytes',x.size_bytes,'schemaVersion',x.schema_version) ORDER BY x.artifact_type,x.id) FROM revision_artifacts x WHERE x.attempt_id=g.id AND x.revision_id=j.revision_id),'[]'::jsonb),
    'packageManifest',(SELECT o.output FROM processing_candidate_outputs o WHERE o.attempt_id=a.id AND o.artifact_type='manifest')) ELSE '{}'::jsonb END
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
 result:=processing_worker_gate_before_streams(p_action,p_job,p_input-'leaseSeconds');
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
 END IF;
 RETURN result;
END $$;

CREATE FUNCTION processing_package_lease_gate(p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE cand record;e integration_outbox_events;result jsonb;i integer;target text:=NULLIF(p_input->>'buildTarget','');
BEGIN
 IF NULLIF(p_input->>'toolchainVersion','') IS NULL OR jsonb_typeof(p_input->'leaseSeconds') IS DISTINCT FROM 'number' THEN RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 -- A worker that died keeps its attempt Running until the lease passes; recover a few here so its job is offered again.
 FOR i IN 1..5 LOOP EXIT WHEN processing_dispatch_gate('Recover')->>'code'<>'OK';END LOOP;
 FOR cand IN SELECT o.idempotency_key,j.id AS job_id,j.input_hash,r.building_id FROM integration_outbox_events o
   JOIN processing_jobs j ON j.id=o.aggregate_id JOIN revisions r ON r.id=j.revision_id
  WHERE o.aggregate_type='ProcessingJob' AND o.event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND o.schema_version='1'
   AND o.available_at<=clock_timestamp() AND (o.status='Pending' OR o.status='Leased' AND o.lease_until<=clock_timestamp())
   AND j.kind IN('PlaytestPackage','ReleasePackage') AND j.status IN('Queued','Running') AND (target IS NULL OR j.input_snapshot->>'buildTarget'=target)
  ORDER BY o.available_at,o.idempotency_key LIMIT 20
 LOOP
  -- Same lock order as the worker gate: identity, Building, then the event row.
  PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||cand.building_id,0));
  SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=cand.idempotency_key AND available_at<=clock_timestamp()
   AND (status='Pending' OR status='Leased' AND lease_until<=clock_timestamp()) FOR UPDATE SKIP LOCKED;
  CONTINUE WHEN e.idempotency_key IS NULL;
  result:=processing_worker_gate('Claim',cand.job_id,jsonb_build_object('eventKey',e.idempotency_key,'payloadHash',e.payload_hash,'inputHash',cand.input_hash,
   'toolchainVersion',p_input->>'toolchainVersion','leaseSeconds',p_input->'leaseSeconds'));
  IF result->>'code'='OK' THEN
   UPDATE integration_outbox_events SET status='Published',lease_owner=NULL,lease_token=NULL,lease_until=NULL,attempts=attempts+1,published_at=clock_timestamp(),
    published_lease_token=(result->>'leaseToken')::uuid,last_error=NULL WHERE idempotency_key=e.idempotency_key;
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

CREATE FUNCTION runtime_package_files(p_kind text,p_actor uuid,p_family uuid,p_id uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE actor users;mid uuid;active boolean;m revision_artifacts;
BEGIN
 SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
 IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
 IF p_kind='Training' THEN
  IF actor.role<>'Trainee' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  SELECT (s.package->>'manifestArtifactId')::uuid,s.status::text IN('Launching','Running') AND s.completion_requested_at IS NULL AND s.launch_family=p_family INTO mid,active
   FROM sessions s WHERE s.id=p_id AND s.contract_version=7 AND s.trainee_user_id=p_actor;
 ELSIF p_kind='Playtest' THEN
  IF actor.role<>'OrganizationUser' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  SELECT pin.manifest_artifact_id,ps.status::text IN('Launching','Running') AND pin.started_family=p_family INTO mid,active
   FROM playtest_sessions ps JOIN playtest_package_pins pin ON pin.playtest_id=ps.id JOIN buildings b ON b.id=ps.building_id
   WHERE ps.id=p_id AND ps.created_by=p_actor AND b.organization_id=actor.organization_id;
 ELSE RETURN jsonb_build_object('code','VALIDATION_ERROR','status',400);END IF;
 IF mid IS NULL THEN RETURN jsonb_build_object('code','NOT_FOUND','status',404);END IF;
 -- Only the login that started the play downloads, and only until it completes.
 IF NOT active THEN RETURN jsonb_build_object('code','PACKAGE_SESSION_NOT_ACTIVE','status',409);END IF;
 SELECT * INTO m FROM revision_artifacts WHERE id=mid AND artifact_type='manifest' AND is_runtime_ready;
 IF m.id IS NULL OR m.schema_version IS DISTINCT FROM 'fet3d.package/1' OR jsonb_typeof(m.metadata->'files') IS DISTINCT FROM 'array' THEN
  RETURN jsonb_build_object('code','PACKAGE_FORMAT_UNSUPPORTED','status',409);
 END IF;
 RETURN jsonb_build_object('code','OK','manifestKey',m.object_key,'manifestSha256',m.sha256_hash,'manifestSizeBytes',m.size_bytes,
  'prefix',left(m.object_key,length(m.object_key)-length('manifest.json')),'files',m.metadata->'files','floors',m.metadata->'floors',
  'runtimeVersion',m.metadata->>'runtimeVersion','buildTarget',m.metadata->>'buildTarget');
END $$;

DO $grants$ DECLARE r text;s record;sig text;BEGIN
 FOREACH sig IN ARRAY ARRAY['processing_dispatch_gate(text,text,uuid,uuid)','accepted_geometry_attempt(uuid)','processing_worker_context(uuid,uuid)',
  'processing_worker_gate(text,uuid,jsonb)','processing_package_lease_gate(jsonb)','runtime_package_files(text,uuid,uuid,uuid)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',sig);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',sig);
  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',sig,r);END IF;END LOOP;
 END LOOP;
 GRANT EXECUTE ON FUNCTION processing_package_lease_gate(jsonb),processing_worker_gate(text,uuid,jsonb),processing_worker_context(uuid,uuid) TO fet3d_processing_executor;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION processing_dispatch_gate(text,text,uuid,uuid),runtime_package_files(text,uuid,uuid,uuid) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM package_pull_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
