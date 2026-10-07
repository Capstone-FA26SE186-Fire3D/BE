CREATE TEMP TABLE redis_recovery_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;hc boolean; BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 hc:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE');INSERT INTO redis_recovery_permissions VALUES(os,oi,c,hc);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE OR REPLACE FUNCTION processing_stream_dispatch_gate(p_action text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE e integration_outbox_events; token uuid; k text:=p_input->>'eventKey'; stream_name text:=p_input->>'streamName'; existing processing_stream_deliveries;
BEGIN
 IF p_action='Claim' THEN
  IF stream_name IS NULL OR length(stream_name)>255 THEN RETURN jsonb_build_object('code','INVALID_STREAM','status',400); END IF;
  SELECT * INTO e FROM integration_outbox_events WHERE aggregate_type='ProcessingJob' AND event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND schema_version='1'
   AND EXISTS(SELECT 1 FROM processing_jobs j WHERE j.id=integration_outbox_events.aggregate_id AND j.status IN('Queued','Running'))
   AND available_at<=clock_timestamp() AND attempts<10 AND (status='Pending' OR status='Leased' AND lease_until<=clock_timestamp())
   ORDER BY available_at,idempotency_key LIMIT 1 FOR UPDATE SKIP LOCKED;
  IF e.idempotency_key IS NULL THEN RETURN jsonb_build_object('code','EMPTY'); END IF;
  token:=gen_random_uuid();
  UPDATE integration_outbox_events SET status='Leased',lease_owner='redis-dispatcher',lease_token=token,lease_until=clock_timestamp()+interval '60 seconds',attempts=attempts+1 WHERE idempotency_key=e.idempotency_key;
  RETURN jsonb_build_object('code','OK','leaseToken',token,'envelope',jsonb_build_object('eventKey',e.idempotency_key,'eventType',e.event_type,'schemaVersion',e.schema_version,
   'aggregateType',e.aggregate_type,'aggregateId',e.aggregate_id,'organizationId',e.organization_id,'payload',e.payload,'payloadHash',e.payload_hash));
 END IF;
 SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=k FOR UPDATE;
 IF e.idempotency_key IS NULL THEN RETURN jsonb_build_object('code','EVENT_NOT_FOUND','status',404); END IF;
 token:=(p_input->>'leaseToken')::uuid;
 IF p_action='Published' THEN
  SELECT * INTO existing FROM processing_stream_deliveries WHERE lease_token=token;
  IF existing.id IS NOT NULL THEN
   IF existing.event_key=k AND existing.stream_name=stream_name AND existing.stream_id=p_input->>'streamId' THEN RETURN jsonb_build_object('code','OK','replayed',true); END IF;
   RETURN jsonb_build_object('code','DELIVERY_CONFLICT','status',409);
  END IF;
 END IF;
 IF e.status<>'Leased' OR e.lease_owner<>'redis-dispatcher' OR e.lease_token IS DISTINCT FROM token OR e.lease_until<=clock_timestamp() THEN RETURN jsonb_build_object('code','LEASE_STALE','status',409); END IF;
 IF p_action='Renew' THEN
  UPDATE integration_outbox_events SET lease_until=greatest(lease_until,clock_timestamp()+interval '60 seconds') WHERE idempotency_key=k;
 ELSIF p_action='Published' THEN
  IF stream_name IS NULL OR length(stream_name)>255 OR COALESCE(p_input->>'streamId','') !~ '^[0-9]+-[0-9]+$' THEN RETURN jsonb_build_object('code','INVALID_STREAM','status',400); END IF;
  INSERT INTO processing_stream_deliveries(event_key,lease_token,stream_name,stream_id) VALUES(k,token,stream_name,p_input->>'streamId');
  UPDATE integration_outbox_events SET status='Published',lease_owner=NULL,lease_token=NULL,lease_until=NULL,published_at=clock_timestamp(),published_lease_token=token,last_error=NULL WHERE idempotency_key=k;
 ELSIF p_action='Fail' THEN
  UPDATE integration_outbox_events SET status=CASE WHEN attempts>=10 THEN 'Failed' ELSE 'Pending' END,lease_owner=NULL,lease_token=NULL,lease_until=NULL,
   available_at=clock_timestamp()+make_interval(secs=>least(1800,30*(2^least(attempts-1,6))::integer)),last_error='REDIS_PUBLICATION_UNAVAILABLE' WHERE idempotency_key=k;
 ELSE RETURN jsonb_build_object('code','DISPATCH_ACTION_INVALID','status',400); END IF;
 RETURN jsonb_build_object('code','OK');
END $$;

ALTER FUNCTION processing_stream_consumer_gate(text,jsonb) RENAME TO processing_stream_consumer_gate_before_recovery;
REVOKE ALL ON FUNCTION processing_stream_consumer_gate_before_recovery(text,jsonb) FROM PUBLIC;
DO $$ DECLARE r text;BEGIN
 FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION processing_stream_consumer_gate_before_recovery(text,jsonb) FROM %I',r);END IF;
 END LOOP;
END $$;
CREATE FUNCTION processing_stream_consumer_gate(p_action text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE result jsonb;BEGIN
 result:=processing_stream_consumer_gate_before_recovery(p_action,p_input);
 IF p_action='Inspect' AND result->>'code'='OK' AND NOT EXISTS(SELECT 1 FROM processing_jobs WHERE id=(p_input->'envelope'->>'aggregateId')::uuid AND status IN('Queued','Running')) THEN RETURN jsonb_build_object('code','JOB_TERMINAL');END IF;
 RETURN result;
END $$;

CREATE FUNCTION processing_stream_recovery_gate(p_action text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE e integration_outbox_events;result jsonb;s processing_stream_state;n integer:=0;timeout_seconds integer;
BEGIN
 IF p_action='RecoverAttempt' THEN RETURN processing_dispatch_gate('Recover');END IF;
 IF p_action='CanTrim' THEN
  result:=processing_stream_consumer_gate('Inspect',p_input);
  RETURN jsonb_build_object('code',CASE WHEN result->>'code'='CONSUMED' THEN 'SAFE' ELSE 'PROTECTED' END);
 END IF;
 IF p_action<>'ReplayMissing' THEN RETURN jsonb_build_object('code','RECOVERY_ACTION_INVALID','status',400);END IF;
 timeout_seconds:=(p_input->>'handoffTimeoutSeconds')::integer;
 IF timeout_seconds IS NULL OR timeout_seconds NOT BETWEEN 60 AND 3600 OR length(COALESCE(p_input->>'streamName','')) NOT BETWEEN 1 AND 255 THEN RETURN jsonb_build_object('code','RECOVERY_INPUT_INVALID','status',400);END IF;
 -- Use the same event->state lock order as the consumer. Never lock a job after its event.
 FOR e IN SELECT * FROM integration_outbox_events o WHERE o.aggregate_type='ProcessingJob'
  AND o.event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND o.schema_version='1'
  AND (o.status='Published' AND o.published_at<=clock_timestamp()-make_interval(secs=>timeout_seconds)
       OR o.status='Leased' AND o.lease_owner='redis-dispatcher' AND o.lease_until<=clock_timestamp() AND o.attempts>=10)
  AND EXISTS(SELECT 1 FROM processing_stream_deliveries d WHERE d.event_key=o.idempotency_key AND d.stream_name=p_input->>'streamName')
  AND NOT EXISTS(SELECT 1 FROM integration_event_consumptions c WHERE c.event_key=o.idempotency_key AND c.consumer_name='processing-bridge-v1')
  ORDER BY o.available_at,o.idempotency_key LIMIT 20 FOR UPDATE SKIP LOCKED
 LOOP
  INSERT INTO processing_stream_state(event_key) VALUES(e.idempotency_key) ON CONFLICT DO NOTHING;
  SELECT * INTO s FROM processing_stream_state WHERE event_key=e.idempotency_key FOR UPDATE;
  IF s.blocked_reason IS NOT NULL OR s.next_delivery_at>clock_timestamp() THEN CONTINUE;END IF;
  IF NOT EXISTS(SELECT 1 FROM processing_jobs j WHERE j.id=e.aggregate_id AND j.status IN('Queued','Running')) THEN
   UPDATE processing_stream_state SET blocked_reason='PROCESSING_JOB_TERMINAL' WHERE event_key=e.idempotency_key;
   CONTINUE;
  END IF;
  IF e.attempts>=10 THEN
   UPDATE integration_outbox_events SET status='Failed',lease_owner=NULL,lease_token=NULL,lease_until=NULL,last_error='REDIS_PUBLICATION_RETRY_EXHAUSTED' WHERE idempotency_key=e.idempotency_key;
   UPDATE processing_stream_state SET blocked_reason='PUBLICATION_RETRY_EXHAUSTED' WHERE event_key=e.idempotency_key;
   CONTINUE;
  END IF;
  UPDATE integration_outbox_events SET status='Pending',lease_owner=NULL,lease_token=NULL,lease_until=NULL,published_at=NULL,
   available_at=clock_timestamp(),last_error='REDIS_HANDOFF_MISSING' WHERE idempotency_key=e.idempotency_key;
  UPDATE processing_stream_state SET last_replay_at=clock_timestamp(),next_delivery_at=clock_timestamp()+make_interval(secs=>least(1800,30*(2^least(e.attempts-1,6))::integer)) WHERE event_key=e.idempotency_key;
  n:=n+1;
 END LOOP;
 -- A cancelled last publication attempt can have no delivery history yet.
 UPDATE integration_outbox_events SET status='Failed',lease_owner=NULL,lease_token=NULL,lease_until=NULL,last_error='REDIS_PUBLICATION_RETRY_EXHAUSTED'
  WHERE aggregate_type='ProcessingJob' AND event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND schema_version='1'
   AND status='Leased' AND lease_owner='redis-dispatcher' AND lease_until<=clock_timestamp() AND attempts>=10;
 RETURN jsonb_build_object('code','OK','replayed',n);
END $$;
DO $$ DECLARE state redis_recovery_permissions;r text;t text;signature text; BEGIN
 ALTER FUNCTION processing_stream_recovery_gate(text,jsonb) OWNER TO fet3d_ifc_upload_owner;
 ALTER FUNCTION processing_stream_consumer_gate(text,jsonb) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION processing_stream_recovery_gate(text,jsonb) FROM PUBLIC;
 REVOKE ALL ON FUNCTION processing_stream_consumer_gate(text,jsonb) FROM PUBLIC;
 REVOKE ALL ON SEQUENCE processing_stream_deliveries_id_seq FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION processing_stream_recovery_gate(text,jsonb),processing_stream_consumer_gate(text,jsonb) TO %I',r);END IF;
 END LOOP;
 -- Supabase default privileges must not expose diagnostics or machine gates to public clients.
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   FOREACH t IN ARRAY ARRAY['processing_stream_deliveries','processing_stream_state','processing_stream_rejections'] LOOP EXECUTE format('REVOKE ALL ON TABLE %I FROM %I',t,r);END LOOP;
   REVOKE ALL ON SEQUENCE processing_stream_deliveries_id_seq FROM PUBLIC;
   EXECUTE format('REVOKE ALL ON SEQUENCE processing_stream_deliveries_id_seq FROM %I',r);
   FOREACH signature IN ARRAY ARRAY['processing_stream_dispatch_gate(text,jsonb)','processing_stream_consumer_gate(text,jsonb)','processing_stream_consumer_gate_before_recovery(text,jsonb)','processing_stream_recovery_gate(text,jsonb)','processing_worker_gate(text,uuid,jsonb)','processing_worker_gate_before_streams(text,uuid,jsonb)'] LOOP EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',signature,r);END LOOP;
  END IF;
 END LOOP;
 SELECT * INTO state FROM redis_recovery_permissions;
 IF NOT state.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF state.changed THEN
  IF state.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN state.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN state.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;
 END IF;
END $$;
