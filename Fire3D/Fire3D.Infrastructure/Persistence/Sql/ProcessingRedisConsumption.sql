CREATE TEMP TABLE redis_consumer_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;hc boolean; BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 hc:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'); INSERT INTO redis_consumer_permissions VALUES(os,oi,c,hc);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 GRANT SELECT,INSERT ON integration_event_consumptions TO fet3d_ifc_upload_owner;
 CREATE POLICY processing_handoff_owner ON integration_event_consumptions TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true);
END $$;

-- Preserve trusted pre-upgrade handoff evidence, without fabricating completed processing.
INSERT INTO integration_event_consumptions(consumer_name,event_key,payload_hash,result_reference,processed_at)
 SELECT 'processing-bridge-v1',event_key,payload_hash,receipt_id::text,created_at FROM processing_delivery_receipts
 ON CONFLICT(consumer_name,event_key) DO NOTHING;
DO $$ BEGIN IF EXISTS(SELECT 1 FROM processing_delivery_receipts r JOIN integration_event_consumptions c ON c.event_key=r.event_key AND c.consumer_name='processing-bridge-v1'
 WHERE c.payload_hash<>r.payload_hash OR c.result_reference<>r.receipt_id::text) THEN RAISE EXCEPTION 'Existing processing handoff receipt conflict';END IF;END $$;

ALTER FUNCTION processing_worker_gate(text,uuid,jsonb) RENAME TO processing_worker_gate_before_streams;
REVOKE ALL ON FUNCTION processing_worker_gate_before_streams(text,uuid,jsonb) FROM PUBLIC,fet3d_processing_executor;
CREATE FUNCTION processing_worker_gate(p_action text,p_job uuid,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE result jsonb;c integration_event_consumptions;k text;h text;r text;
BEGIN
 result:=processing_worker_gate_before_streams(p_action,p_job,p_input);
 IF p_action='Claim' AND result->>'code'='OK' THEN
  k:=p_input->>'eventKey';h:=p_input->>'payloadHash';r:=result->>'receiptId';
  INSERT INTO integration_event_consumptions(consumer_name,event_key,payload_hash,result_reference) VALUES('processing-bridge-v1',k,h,r) ON CONFLICT(consumer_name,event_key) DO NOTHING;
  SELECT * INTO c FROM integration_event_consumptions WHERE consumer_name='processing-bridge-v1' AND event_key=k;
  IF c.payload_hash IS DISTINCT FROM h OR c.result_reference IS DISTINCT FROM r THEN RAISE EXCEPTION 'Processing handoff receipt conflict';END IF;
 END IF;
 RETURN result;
END $$;

CREATE FUNCTION processing_stream_consumer_gate(p_action text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE env jsonb:=p_input->'envelope';k text:=env->>'eventKey';e integration_outbox_events;c integration_event_consumptions;s processing_stream_state;
BEGIN
 IF p_action='Reject' THEN
  IF p_input->>'streamName' IS NULL OR length(p_input->>'streamName')>255 OR COALESCE(p_input->>'streamId','') !~ '^[0-9]+-[0-9]+$' THEN RETURN jsonb_build_object('code','INVALID_STREAM','status',400);END IF;
  INSERT INTO processing_stream_rejections(stream_name,stream_id,event_key,code) VALUES(p_input->>'streamName',p_input->>'streamId',left(p_input->>'eventKey',255),left(COALESCE(p_input->>'code','INVALID_ENVELOPE'),100)) ON CONFLICT DO NOTHING;
  RETURN jsonb_build_object('code','REJECTED');
 END IF;
 SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=k FOR UPDATE;
 IF e.idempotency_key IS NULL OR e.aggregate_type<>'ProcessingJob' OR e.event_type NOT IN('ProcessingJobRequested','ProcessingJobRequeue') OR e.schema_version<>'1'
  OR env->>'aggregateType' IS DISTINCT FROM e.aggregate_type OR env->>'aggregateId' IS DISTINCT FROM e.aggregate_id::text OR env->>'eventType' IS DISTINCT FROM e.event_type
  OR env->>'schemaVersion' IS DISTINCT FROM e.schema_version OR env->>'organizationId' IS DISTINCT FROM e.organization_id::text OR env->'payload' IS DISTINCT FROM e.payload
  OR env->>'payloadHash' IS DISTINCT FROM e.payload_hash OR NOT EXISTS(SELECT 1 FROM processing_jobs j JOIN revisions r ON r.id=j.revision_id JOIN buildings b ON b.id=r.building_id WHERE j.id=e.aggregate_id AND b.organization_id=e.organization_id)
 THEN
  IF p_action='Inspect' THEN PERFORM processing_stream_consumer_gate('Reject',jsonb_build_object('streamName',p_input->>'streamName','streamId',p_input->>'streamId','eventKey',k,'code','ENVELOPE_CONFLICT'));END IF;
  RETURN jsonb_build_object('code','ENVELOPE_CONFLICT','status',409);
 END IF;
 SELECT * INTO c FROM integration_event_consumptions WHERE consumer_name='processing-bridge-v1' AND event_key=k;
 IF c.event_key IS NOT NULL THEN
  IF c.payload_hash IS DISTINCT FROM e.payload_hash OR NOT EXISTS(SELECT 1 FROM processing_delivery_receipts WHERE event_key=k AND payload_hash=e.payload_hash AND receipt_id::text=c.result_reference)
   THEN RETURN jsonb_build_object('code','RECEIPT_CONFLICT','status',409);END IF;
  IF p_action='Confirm' AND p_input->>'receiptId' IS DISTINCT FROM c.result_reference THEN RETURN jsonb_build_object('code','RECEIPT_CONFLICT','status',409);END IF;
  RETURN jsonb_build_object('code','CONSUMED','receiptId',c.result_reference);
 END IF;
 IF p_action='Confirm' THEN RETURN jsonb_build_object('code','RECEIPT_REQUIRED','status',409);END IF;
 INSERT INTO processing_stream_state(event_key) VALUES(k) ON CONFLICT DO NOTHING;
 SELECT * INTO s FROM processing_stream_state WHERE event_key=k FOR UPDATE;
 IF p_action='Inspect' THEN
  IF s.blocked_reason IS NOT NULL OR s.delivery_failures>=10 THEN RETURN jsonb_build_object('code','BLOCKED');END IF;
  IF s.next_delivery_at>clock_timestamp() THEN RETURN jsonb_build_object('code','DEFERRED');END IF;
  RETURN jsonb_build_object('code','OK');
 ELSIF p_action='Failure' THEN
  UPDATE processing_stream_state SET delivery_failures=delivery_failures+1,next_delivery_at=clock_timestamp()+make_interval(secs=>least(1800,30*(2^least(delivery_failures,6))::integer)),
   blocked_reason=CASE WHEN delivery_failures+1>=10 THEN 'HANDOFF_RETRY_EXHAUSTED' ELSE NULL END,last_error='WORKER_HANDOFF_UNAVAILABLE' WHERE event_key=k;
  RETURN jsonb_build_object('code','RETRY_RECORDED');
 END IF;
 RETURN jsonb_build_object('code','CONSUMER_ACTION_INVALID','status',400);
END $$;

DO $$ DECLARE state redis_consumer_permissions;r text;signature text; BEGIN
 FOREACH signature IN ARRAY ARRAY['processing_worker_gate(text,uuid,jsonb)','processing_stream_consumer_gate(text,jsonb)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_ifc_upload_owner',signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
 END LOOP;
 GRANT EXECUTE ON FUNCTION processing_worker_gate(text,uuid,jsonb) TO fet3d_processing_executor;
 FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('GRANT EXECUTE ON FUNCTION processing_stream_consumer_gate(text,jsonb) TO %I',r);END IF;
 END LOOP;
 SELECT * INTO state FROM redis_consumer_permissions;
 IF NOT state.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF state.changed THEN
  IF state.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN state.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN state.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;
 END IF;
END $$;
