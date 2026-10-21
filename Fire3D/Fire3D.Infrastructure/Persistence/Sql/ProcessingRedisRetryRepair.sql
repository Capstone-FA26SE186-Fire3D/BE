-- Forward repair: keep historical envelopes, publication evidence and worker receipts intact.
CREATE TEMP TABLE redis_retry_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO redis_retry_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE TABLE processing_stream_retry_receipts (
 request_key text PRIMARY KEY CHECK(length(request_key) BETWEEN 1 AND 255),
 job_id uuid NOT NULL REFERENCES processing_jobs(id),
 event_key text NOT NULL REFERENCES integration_outbox_events(idempotency_key),
 reason text NOT NULL CHECK(length(reason) BETWEEN 1 AND 1000),
 previous_delivery jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
ALTER TABLE processing_stream_retry_receipts ENABLE ROW LEVEL SECURITY;
CREATE POLICY processing_retry_owner ON processing_stream_retry_receipts TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
GRANT SELECT,INSERT ON processing_stream_retry_receipts TO fet3d_ifc_upload_owner;
REVOKE ALL ON processing_stream_retry_receipts FROM PUBLIC;

ALTER FUNCTION processing_stream_recovery_gate(text,jsonb) RENAME TO processing_stream_recovery_gate_before_retry_repair;
CREATE FUNCTION processing_stream_recovery_gate(p_action text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE e integration_outbox_events;s processing_stream_state;n integer:=0;timeout_seconds integer;blocked bigint;
BEGIN
 IF p_action<>'ReplayMissing' THEN RETURN processing_stream_recovery_gate_before_retry_repair(p_action,p_input);END IF;
 timeout_seconds:=(p_input->>'handoffTimeoutSeconds')::integer;
 IF timeout_seconds IS NULL OR timeout_seconds NOT BETWEEN 60 AND 3600 OR length(COALESCE(p_input->>'streamName','')) NOT BETWEEN 1 AND 255 THEN RETURN jsonb_build_object('code','RECOVERY_INPUT_INVALID','status',400);END IF;
 -- Filter protected rows BEFORE LIMIT; otherwise twenty permanently blocked rows starve all later work.
 -- Preserve event -> state lock ordering and recheck state after acquiring its lock.
 FOR e IN SELECT * FROM integration_outbox_events o WHERE o.aggregate_type='ProcessingJob'
  AND o.event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND o.schema_version='1'
  AND (o.status='Published' AND o.published_at<=clock_timestamp()-make_interval(secs=>timeout_seconds)
       OR o.status='Leased' AND o.lease_owner='redis-dispatcher' AND o.lease_until<=clock_timestamp() AND o.attempts>=10)
  AND EXISTS(SELECT 1 FROM processing_stream_deliveries d WHERE d.event_key=o.idempotency_key AND d.stream_name=p_input->>'streamName')
  AND NOT EXISTS(SELECT 1 FROM integration_event_consumptions c WHERE c.event_key=o.idempotency_key AND c.consumer_name='processing-bridge-v1')
  AND NOT EXISTS(SELECT 1 FROM processing_stream_state st WHERE st.event_key=o.idempotency_key
      AND (st.blocked_reason IS NOT NULL OR st.delivery_failures>=10 OR st.next_delivery_at>clock_timestamp()))
  ORDER BY o.available_at,o.idempotency_key LIMIT 20 FOR UPDATE SKIP LOCKED
 LOOP
  INSERT INTO processing_stream_state(event_key) VALUES(e.idempotency_key) ON CONFLICT DO NOTHING;
  SELECT * INTO s FROM processing_stream_state WHERE event_key=e.idempotency_key FOR UPDATE;
  IF s.blocked_reason IS NOT NULL OR s.delivery_failures>=10 OR s.next_delivery_at>clock_timestamp() THEN CONTINUE;END IF;
  IF NOT EXISTS(SELECT 1 FROM processing_jobs j WHERE j.id=e.aggregate_id AND j.status IN('Queued','Running')) THEN
   UPDATE processing_stream_state SET blocked_reason='PROCESSING_JOB_TERMINAL' WHERE event_key=e.idempotency_key;
   CONTINUE;
  END IF;
  IF e.attempts>=10 THEN
   UPDATE integration_outbox_events SET status='Failed',lease_owner=NULL,lease_token=NULL,lease_until=NULL,published_at=NULL,last_error='REDIS_PUBLICATION_RETRY_EXHAUSTED' WHERE idempotency_key=e.idempotency_key;
   UPDATE processing_stream_state SET blocked_reason='PUBLICATION_RETRY_EXHAUSTED' WHERE event_key=e.idempotency_key;
   CONTINUE;
  END IF;
  UPDATE integration_outbox_events SET status='Pending',lease_owner=NULL,lease_token=NULL,lease_until=NULL,published_at=NULL,
   available_at=clock_timestamp(),last_error='REDIS_HANDOFF_MISSING' WHERE idempotency_key=e.idempotency_key;
  UPDATE processing_stream_state SET last_replay_at=clock_timestamp(),next_delivery_at=clock_timestamp()+make_interval(secs=>least(1800,30*(2^least(e.attempts-1,6))::integer)) WHERE event_key=e.idempotency_key;
  n:=n+1;
 END LOOP;
 UPDATE integration_outbox_events SET status='Failed',lease_owner=NULL,lease_token=NULL,lease_until=NULL,last_error='REDIS_PUBLICATION_RETRY_EXHAUSTED'
  WHERE aggregate_type='ProcessingJob' AND event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND schema_version='1'
   AND status='Leased' AND lease_owner='redis-dispatcher' AND lease_until<=clock_timestamp() AND attempts>=10;
 SELECT count(*) INTO blocked FROM integration_outbox_events o
  WHERE o.aggregate_type='ProcessingJob' AND o.event_type IN('ProcessingJobRequested','ProcessingJobRequeue')
   AND EXISTS(SELECT 1 FROM processing_jobs j WHERE j.id=o.aggregate_id AND j.status='Queued')
   AND NOT EXISTS(SELECT 1 FROM processing_delivery_receipts r WHERE r.event_key=o.idempotency_key)
   AND (o.status='Failed' AND o.last_error IN('REDIS_PUBLICATION_UNAVAILABLE','REDIS_PUBLICATION_RETRY_EXHAUSTED')
        OR EXISTS(SELECT 1 FROM processing_stream_state st WHERE st.event_key=o.idempotency_key AND st.blocked_reason IN('HANDOFF_RETRY_EXHAUSTED','PUBLICATION_RETRY_EXHAUSTED')));
 RETURN jsonb_build_object('code','OK','replayed',n,'blocked',blocked);
END $$;

ALTER FUNCTION requeue_processing_job(uuid,text,text) RENAME TO requeue_processing_job_before_stream_retry;
CREATE FUNCTION requeue_processing_job(p_job uuid,p_key text,p_reason text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE j processing_jobs;e integration_outbox_events;s processing_stream_state;receipt processing_stream_retry_receipts;building uuid;
BEGIN
 IF NULLIF(btrim(p_key),'') IS NULL OR length(p_key)>255 OR NULLIF(btrim(p_reason),'') IS NULL OR length(p_reason)>1000 THEN
  RAISE EXCEPTION 'invalid processing delivery retry request';
 END IF;
 -- Same lifecycle/building -> event -> job order as worker Claim. This also serializes different retry keys.
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT r.building_id INTO building FROM processing_jobs pj JOIN revisions r ON r.id=pj.revision_id WHERE pj.id=p_job;
 IF building IS NULL THEN RETURN 'NotClaimable';END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:building:'||building,0));
 PERFORM pg_advisory_xact_lock(hashtextextended(p_key,0));
 SELECT * INTO receipt FROM processing_stream_retry_receipts WHERE request_key=p_key;
 IF receipt.request_key IS NOT NULL THEN
  IF receipt.job_id IS DISTINCT FROM p_job OR receipt.reason IS DISTINCT FROM p_reason THEN RAISE EXCEPTION 'requeue idempotency key conflicts with a different envelope';END IF;
  RETURN 'AlreadyRequeued';
 END IF;
 -- Retain legacy failed-job retry and its immutable request-key conflict behavior.
 IF EXISTS(SELECT 1 FROM integration_outbox_events WHERE idempotency_key=p_key) THEN
  RETURN requeue_processing_job_before_stream_retry(p_job,p_key,p_reason);
 END IF;
 SELECT * INTO e FROM integration_outbox_events o WHERE o.aggregate_type='ProcessingJob' AND o.aggregate_id=p_job
  AND o.event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND o.schema_version='1'
  ORDER BY o.created_at DESC,o.idempotency_key DESC LIMIT 1 FOR UPDATE;
 SELECT * INTO j FROM processing_jobs WHERE id=p_job FOR UPDATE;
 IF j.status='Failed' THEN RETURN requeue_processing_job_before_stream_retry(p_job,p_key,p_reason);END IF;
 IF j.status<>'Queued' OR e.idempotency_key IS NULL
  OR EXISTS(SELECT 1 FROM processing_job_attempts WHERE id=j.current_attempt_id AND status='Running')
  OR EXISTS(SELECT 1 FROM processing_delivery_receipts WHERE event_key=e.idempotency_key)
  OR EXISTS(SELECT 1 FROM integration_event_consumptions WHERE event_key=e.idempotency_key AND consumer_name='processing-bridge-v1')
 THEN RETURN 'NotClaimable';END IF;
 SELECT * INTO s FROM processing_stream_state WHERE event_key=e.idempotency_key FOR UPDATE;
 IF NOT COALESCE(e.status='Failed' AND e.attempts>=10 AND e.last_error IN('REDIS_PUBLICATION_UNAVAILABLE','REDIS_PUBLICATION_RETRY_EXHAUSTED'),false)
    AND COALESCE(s.blocked_reason,'') NOT IN('HANDOFF_RETRY_EXHAUSTED','PUBLICATION_RETRY_EXHAUSTED') THEN RETURN 'NotClaimable';END IF;
 -- A delivery retry reuses the SAME event/hash. Late Redis messages therefore still deduplicate against its receipt.
 INSERT INTO processing_stream_retry_receipts(request_key,job_id,event_key,reason,previous_delivery)
 VALUES(p_key,p_job,e.idempotency_key,p_reason,jsonb_build_object('status',e.status,'attempts',e.attempts,'lastError',e.last_error,
  'deliveryFailures',s.delivery_failures,'blockedReason',s.blocked_reason,'handoffError',s.last_error));
 UPDATE integration_outbox_events SET status='Pending',attempts=0,available_at=clock_timestamp(),
  lease_owner=NULL,lease_token=NULL,lease_until=NULL,published_at=NULL,published_lease_token=NULL,last_error=NULL WHERE idempotency_key=e.idempotency_key;
 INSERT INTO processing_stream_state(event_key) VALUES(e.idempotency_key)
 ON CONFLICT(event_key) DO UPDATE SET delivery_failures=0,next_delivery_at=clock_timestamp(),blocked_reason=NULL,last_error=NULL;
 RETURN 'Requeued';
END $$;

DO $$ DECLARE state redis_retry_permissions;r text;signature text;BEGIN
 ALTER FUNCTION processing_stream_recovery_gate(text,jsonb) OWNER TO fet3d_ifc_upload_owner;
 ALTER FUNCTION requeue_processing_job(uuid,text,text) OWNER TO fet3d_ifc_upload_owner;
 FOREACH signature IN ARRAY ARRAY['processing_stream_recovery_gate(text,jsonb)','processing_stream_recovery_gate_before_retry_repair(text,jsonb)',
  'requeue_processing_job(uuid,text,text)','requeue_processing_job_before_stream_retry(uuid,text,text)'] LOOP
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
  FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor','anon','authenticated'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',signature,r);END IF;
  END LOOP;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   EXECUTE format('GRANT EXECUTE ON FUNCTION processing_stream_recovery_gate(text,jsonb),requeue_processing_job(uuid,text,text) TO %I',r);
  END IF;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['anon','authenticated','fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON processing_stream_retry_receipts FROM %I',r);END IF;
 END LOOP;
 SELECT * INTO state FROM redis_retry_permissions;
 IF NOT state.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF state.changed THEN
  IF state.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN state.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN state.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;
 END IF;
END $$;
