-- Consumer for PlatformCacheInvalidation (Learn/Library/knowledge changes). Until now nothing claimed these rows, so they stayed Pending.
-- The API worker claims one due event, bumps the cache version after commit of the source change, then records the receipt and closes
-- the row in one transaction. A crash between the bump and the receipt only repeats an extra (harmless) invalidation.
CREATE TEMP TABLE platform_cache_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO platform_cache_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE FUNCTION platform_cache_invalidation_gate(p_action text,p_input jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE e integration_outbox_events;token uuid;k text:=p_input->>'eventKey';receipt integration_event_consumptions;
 consumer constant text:='platform-cache-invalidation';
BEGIN
 IF p_action='Claim' THEN
  SELECT * INTO e FROM integration_outbox_events WHERE aggregate_type='Platform' AND event_type='PlatformCacheInvalidation' AND schema_version='1'
   AND available_at<=clock_timestamp() AND attempts<10 AND (status='Pending' OR status='Leased' AND lease_until<=clock_timestamp())
   ORDER BY available_at,idempotency_key LIMIT 1 FOR UPDATE SKIP LOCKED;
  IF e.idempotency_key IS NULL THEN RETURN jsonb_build_object('code','EMPTY');END IF;
  token:=gen_random_uuid();
  UPDATE integration_outbox_events SET status='Leased',lease_owner=consumer,lease_token=token,lease_until=clock_timestamp()+interval '60 seconds',attempts=attempts+1
   WHERE idempotency_key=e.idempotency_key;
  RETURN jsonb_build_object('code','OK','eventKey',e.idempotency_key,'leaseToken',token,'payload',e.payload);
 END IF;
 IF p_action NOT IN('Complete','Fail') THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 IF COALESCE(p_input->>'leaseToken','') !~ '^[0-9a-fA-F-]{36}$' THEN RETURN jsonb_build_object('code','LEASE_STALE','status',409);END IF;
 token:=(p_input->>'leaseToken')::uuid;
 SELECT * INTO e FROM integration_outbox_events WHERE idempotency_key=k AND aggregate_type='Platform' AND event_type='PlatformCacheInvalidation' FOR UPDATE;
 IF e.idempotency_key IS NULL THEN RETURN jsonb_build_object('code','EVENT_NOT_FOUND','status',404);END IF;
 IF e.status='Published' AND e.published_lease_token=token AND p_action='Complete' THEN RETURN jsonb_build_object('code','OK','replayed',true);END IF;
 -- A later claim owns the row once this lease was taken over; completing or failing with the old token is refused.
 IF e.status<>'Leased' OR e.lease_owner<>consumer OR e.lease_token IS DISTINCT FROM token THEN RETURN jsonb_build_object('code','LEASE_STALE','status',409);END IF;
 IF p_action='Complete' THEN
  SELECT * INTO receipt FROM integration_event_consumptions WHERE consumer_name=consumer AND event_key=k;
  IF receipt.event_key IS NULL THEN
   INSERT INTO integration_event_consumptions(consumer_name,event_key,payload_hash,result_reference) VALUES(consumer,k,e.payload_hash,left(p_input->>'result',200));
  ELSIF receipt.payload_hash<>e.payload_hash THEN RETURN jsonb_build_object('code','RECEIPT_CONFLICT','status',409);
  END IF;
  UPDATE integration_outbox_events SET status='Published',lease_owner=NULL,lease_token=NULL,lease_until=NULL,published_at=clock_timestamp(),published_lease_token=token,last_error=NULL
   WHERE idempotency_key=k;
 ELSE
  UPDATE integration_outbox_events SET status=CASE WHEN attempts>=10 THEN 'Failed' ELSE 'Pending' END,lease_owner=NULL,lease_token=NULL,lease_until=NULL,
   available_at=clock_timestamp()+make_interval(secs=>least(1800,30*(2^least(attempts-1,6))::integer)),
   last_error=CASE WHEN p_input->>'error' ~ '^[A-Z_]{1,64}$' THEN p_input->>'error' ELSE 'CACHE_INVALIDATION_FAILED' END
   WHERE idempotency_key=k;
 END IF;
 RETURN jsonb_build_object('code','OK');
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION platform_cache_invalidation_gate(text,jsonb) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION platform_cache_invalidation_gate(text,jsonb) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('REVOKE ALL ON FUNCTION platform_cache_invalidation_gate(text,jsonb) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION platform_cache_invalidation_gate(text,jsonb) TO %I',r);END IF;END LOOP;
 SELECT * INTO s FROM platform_cache_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
