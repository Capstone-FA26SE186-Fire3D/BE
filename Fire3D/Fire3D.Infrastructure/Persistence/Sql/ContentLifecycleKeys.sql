-- Optional Idempotency-Key for If-Match content mutations (#57): Learn version update/publish and post hide/show/delete/restore,
-- Library item update and version update/publish. With a key, the first successful response is stored and replayed for the same
-- key and input, even after the ETag moved; another input under the key is 409 IDEMPOTENCY_KEY_CONFLICT. Without a key the
-- existing gates run unchanged. The inner gates still own validation, If-Match, audit and cache invalidation.
CREATE TEMP TABLE content_lifecycle_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_ifc_upload_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') OR NOT pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE');
 INSERT INTO content_lifecycle_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_ifc_upload_owner;
END $$;

CREATE FUNCTION content_lifecycle_gate(p_gate text,p_action text,p_actor uuid,p_family uuid,p_resource uuid,p_input jsonb,p_key text,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE op text:='Lifecycle:'||p_gate||':'||p_action;h text;receipt content_command_receipts;actor users;result jsonb;
BEGIN
 IF NOT ((p_gate='learn' AND p_action IN('UpdateVersion','PublishVersion','Hide','Show','Delete','Restore'))
  OR (p_gate='library' AND p_action IN('UpdateItem','UpdateVersion','PublishVersion'))) THEN RETURN jsonb_build_object('code','ACTION_INVALID','status',400);END IF;
 IF p_key IS NOT NULL THEN
  IF length(p_key) NOT BETWEEN 1 AND 128 OR p_key ~ '[[:space:][:cntrl:]]' THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_REQUIRED','status',400);END IF;
  -- A replay answers only the same live PlatformAdmin, like the first call.
  SELECT * INTO actor FROM users WHERE id=p_actor AND is_active AND deleted_at IS NULL;
  IF actor.id IS NULL OR NOT fet3d_live_family(p_actor,p_family) THEN RETURN jsonb_build_object('code','UNAUTHORIZED','status',401);END IF;
  IF actor.role<>'PlatformAdmin' THEN RETURN jsonb_build_object('code','FORBIDDEN','status',403);END IF;
  h:=fet3d_jsonb_payload_hash(jsonb_build_object('resource',p_resource,'input',p_input,'expected',p_expected));
  PERFORM pg_advisory_xact_lock(hashtextextended('content:'||p_actor||':'||op||':'||p_key,0));
  SELECT * INTO receipt FROM content_command_receipts WHERE actor_id=p_actor AND operation=op AND idempotency_key=p_key;
  IF receipt.result IS NOT NULL THEN
   IF receipt.input_hash<>h THEN RETURN jsonb_build_object('code','IDEMPOTENCY_KEY_CONFLICT','status',409);END IF;
   RETURN jsonb_build_object('code','OK','result',receipt.result);
  END IF;
 END IF;
 result:=CASE p_gate WHEN 'learn' THEN learn_admin_gate(p_action,p_actor,p_family,p_resource,p_input,NULL,p_expected)
  ELSE library_gate(p_action,p_actor,p_family,p_resource,p_input,NULL,p_expected) END;
 IF p_key IS NOT NULL AND result->>'code'='OK' THEN INSERT INTO content_command_receipts VALUES(p_actor,op,p_key,h,result->'result',clock_timestamp());END IF;
 RETURN result;
END $$;

DO $grants$ DECLARE r text;s record;BEGIN
 ALTER FUNCTION content_lifecycle_gate(text,text,uuid,uuid,uuid,jsonb,text,bigint) OWNER TO fet3d_ifc_upload_owner;
 REVOKE ALL ON FUNCTION content_lifecycle_gate(text,text,uuid,uuid,uuid,jsonb,text,bigint) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON FUNCTION content_lifecycle_gate(text,text,uuid,uuid,uuid,jsonb,text,bigint) FROM %I',r);END IF;END LOOP;
 FOREACH r IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
  EXECUTE format('GRANT EXECUTE ON FUNCTION content_lifecycle_gate(text,text,uuid,uuid,uuid,jsonb,text,bigint) TO %I',r);
 END IF;END LOOP;
 SELECT * INTO s FROM content_lifecycle_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_ifc_upload_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_ifc_upload_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_ifc_upload_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
