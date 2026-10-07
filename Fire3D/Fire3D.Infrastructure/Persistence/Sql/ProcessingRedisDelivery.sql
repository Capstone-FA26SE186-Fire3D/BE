-- Redis publication is separate from durable worker handoff. No legacy envelope is rewritten.
CREATE TABLE processing_stream_deliveries (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 event_key text NOT NULL REFERENCES integration_outbox_events(idempotency_key),
 lease_token uuid NOT NULL UNIQUE,stream_name text NOT NULL,stream_id text NOT NULL CHECK(stream_id ~ '^[0-9]+-[0-9]+$'),
 published_at timestamptz NOT NULL DEFAULT clock_timestamp(),UNIQUE(stream_name,stream_id)
);
CREATE INDEX processing_stream_delivery_event ON processing_stream_deliveries(event_key,published_at);
CREATE TABLE processing_stream_state (
 event_key text PRIMARY KEY REFERENCES integration_outbox_events(idempotency_key),
 delivery_failures integer NOT NULL DEFAULT 0 CHECK(delivery_failures>=0),
 next_delivery_at timestamptz NOT NULL DEFAULT clock_timestamp(),blocked_reason text,
 last_replay_at timestamptz, last_error text
);
CREATE TABLE processing_stream_rejections (
 stream_name text NOT NULL,stream_id text NOT NULL,event_key text,code text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),PRIMARY KEY(stream_name,stream_id)
);
CREATE TEMP TABLE redis_owner_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;hc boolean;t text; BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 hc:=has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'); INSERT INTO redis_owner_permissions VALUES(os,oi,c,hc);
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user); END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
 FOREACH t IN ARRAY ARRAY['processing_stream_deliveries','processing_stream_state','processing_stream_rejections'] LOOP
  EXECUTE format('GRANT SELECT,INSERT,UPDATE ON %I TO fet3d_ifc_upload_owner',t);
  EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY processing_stream_owner ON %I TO fet3d_ifc_upload_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('REVOKE ALL ON %I FROM PUBLIC',t);
 END LOOP;
 GRANT USAGE ON SEQUENCE processing_stream_deliveries_id_seq TO fet3d_ifc_upload_owner;
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_dispatcher_executor') THEN CREATE ROLE fet3d_dispatcher_executor NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_dispatcher_executor' AND (rolsuper OR rolbypassrls OR rolcanlogin OR rolcreatedb OR rolcreaterole OR rolreplication)) THEN RAISE EXCEPTION 'dispatcher executor must be a restricted NOLOGIN role'; END IF;
END $$;

CREATE FUNCTION processing_stream_dispatch_gate(p_action text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE e integration_outbox_events; token uuid; k text:=p_input->>'eventKey'; stream_name text:=p_input->>'streamName'; existing processing_stream_deliveries;
BEGIN
 IF p_action='Claim' THEN
  IF stream_name IS NULL OR length(stream_name)>255 THEN RETURN jsonb_build_object('code','INVALID_STREAM','status',400); END IF;
  SELECT * INTO e FROM integration_outbox_events WHERE aggregate_type='ProcessingJob' AND event_type IN('ProcessingJobRequested','ProcessingJobRequeue') AND schema_version='1'
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
DO $$ DECLARE state redis_owner_permissions; BEGIN
 ALTER FUNCTION processing_stream_dispatch_gate(text,jsonb) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION processing_stream_dispatch_gate(text,jsonb) FROM PUBLIC;
 GRANT EXECUTE ON FUNCTION processing_stream_dispatch_gate(text,jsonb) TO fet3d_dispatcher_executor;
 SELECT * INTO state FROM redis_owner_permissions;
 IF NOT state.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner; END IF;
 IF state.changed THEN
  IF state.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN state.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN state.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;
 END IF;
END $$;
