-- A bounded maintenance entrypoint; backend callers never gain general DELETE.
CREATE FUNCTION public.cleanup_expired_refresh_family(p_user uuid,p_family uuid,p_retention_days integer)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public
AS $$
DECLARE removed integer;
BEGIN
  IF p_user IS NULL OR p_family IS NULL OR p_retention_days IS NULL OR p_retention_days NOT BETWEEN 1 AND 365 THEN
    RAISE EXCEPTION 'invalid refresh cleanup scope or retention';
  END IF;
  PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_user::text,0));
  DELETE FROM public.auth_refresh_tokens token
  WHERE token.user_id=p_user AND token.family_id=p_family
    AND NOT EXISTS(SELECT 1 FROM public.auth_refresh_tokens member
      WHERE member.user_id=p_user AND member.family_id=p_family
        AND member.expires_at > clock_timestamp() - (p_retention_days * interval '1 day'));
  GET DIAGNOSTICS removed=ROW_COUNT;
  RETURN removed;
END $$;

DO $permissions$
DECLARE was_member boolean; had_create boolean; backend_role text; client_role text;
BEGIN
  IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_auth_maintenance_owner') THEN
    CREATE ROLE fet3d_auth_maintenance_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
  was_member:=pg_has_role(current_user,'fet3d_auth_maintenance_owner','MEMBER');
  had_create:=has_schema_privilege('fet3d_auth_maintenance_owner','public','CREATE');
  IF NOT was_member THEN EXECUTE format('GRANT fet3d_auth_maintenance_owner TO %I',current_user); END IF;
  GRANT USAGE,CREATE ON SCHEMA public TO fet3d_auth_maintenance_owner;
  GRANT SELECT,DELETE ON auth_refresh_tokens TO fet3d_auth_maintenance_owner;
  CREATE POLICY refresh_maintenance_access ON auth_refresh_tokens TO fet3d_auth_maintenance_owner USING(true);
  ALTER FUNCTION public.cleanup_expired_refresh_family(uuid,uuid,integer) OWNER TO fet3d_auth_maintenance_owner;
  REVOKE ALL ON FUNCTION public.cleanup_expired_refresh_family(uuid,uuid,integer) FROM PUBLIC;
  FOREACH client_role IN ARRAY ARRAY['anon','authenticated'] LOOP
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client_role) THEN
      EXECUTE format('REVOKE ALL ON FUNCTION public.cleanup_expired_refresh_family(uuid,uuid,integer) FROM %I',client_role);
    END IF;
  END LOOP;
  FOREACH backend_role IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=backend_role) THEN
      EXECUTE format('GRANT EXECUTE ON FUNCTION public.cleanup_expired_refresh_family(uuid,uuid,integer) TO %I',backend_role);
    END IF;
  END LOOP;
  IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_auth_maintenance_owner; END IF;
  IF NOT was_member THEN EXECUTE format('REVOKE fet3d_auth_maintenance_owner FROM %I',current_user); END IF;
END $permissions$;
