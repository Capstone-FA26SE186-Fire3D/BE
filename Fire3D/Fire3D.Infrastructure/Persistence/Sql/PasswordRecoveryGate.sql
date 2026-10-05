-- Only the backend can invalidate legacy reset tokens. Preserve rows for recovery/history.
CREATE FUNCTION public.invalidate_legacy_reset_tokens(p_user uuid)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public
AS $$
DECLARE changed integer;
BEGIN
  IF p_user IS NULL OR p_user='00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'invalid password recovery scope';
  END IF;
  PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
  PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_user::text,0));
  UPDATE public.password_reset_tokens SET used_at=clock_timestamp() WHERE user_id=p_user AND used_at IS NULL;
  GET DIAGNOSTICS changed=ROW_COUNT;
  RETURN changed;
END $$;

ALTER TABLE public.password_reset_tokens ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.password_reset_tokens FROM PUBLIC;

DO $permissions$
DECLARE original_set boolean; original_inherit boolean; changed_membership boolean; had_create boolean; backend_role text; client_role text;
BEGIN
  IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_password_recovery_owner') THEN
    CREATE ROLE fet3d_password_recovery_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_password_recovery_owner'
    AND (rolcanlogin OR rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication)) THEN
    RAISE EXCEPTION 'password recovery owner must be a restricted NOLOGIN role';
  END IF;
  SELECT set_option,inherit_option INTO original_set,original_inherit FROM pg_auth_members
    WHERE roleid='fet3d_password_recovery_owner'::regrole AND member=current_user::regrole;
  changed_membership:=NOT pg_has_role(current_user,'fet3d_password_recovery_owner','SET')
    OR NOT pg_has_role(current_user,'fet3d_password_recovery_owner','USAGE');
  had_create:=has_schema_privilege('fet3d_password_recovery_owner','public','CREATE');
  IF changed_membership THEN EXECUTE format('GRANT fet3d_password_recovery_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user); END IF;
  GRANT USAGE,CREATE ON SCHEMA public TO fet3d_password_recovery_owner;
  GRANT SELECT ON public.password_reset_tokens TO fet3d_password_recovery_owner;
  GRANT UPDATE(used_at) ON public.password_reset_tokens TO fet3d_password_recovery_owner;
  CREATE POLICY password_recovery_owner_access ON public.password_reset_tokens TO fet3d_password_recovery_owner USING(true) WITH CHECK(true);
  ALTER FUNCTION public.invalidate_legacy_reset_tokens(uuid) OWNER TO fet3d_password_recovery_owner;
  REVOKE ALL ON FUNCTION public.invalidate_legacy_reset_tokens(uuid) FROM PUBLIC;
  FOREACH client_role IN ARRAY ARRAY['anon','authenticated'] LOOP
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client_role) THEN
      EXECUTE format('REVOKE ALL ON public.password_reset_tokens FROM %I',client_role);
      EXECUTE format('REVOKE ALL ON FUNCTION public.invalidate_legacy_reset_tokens(uuid) FROM %I',client_role);
    END IF;
  END LOOP;
  FOREACH backend_role IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=backend_role) THEN
      EXECUTE format('GRANT EXECUTE ON FUNCTION public.invalidate_legacy_reset_tokens(uuid) TO %I',backend_role);
    END IF;
  END LOOP;
  IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_password_recovery_owner; END IF;
  IF changed_membership THEN
    IF original_set IS NULL THEN EXECUTE format('REVOKE fet3d_password_recovery_owner FROM %I',current_user);
    ELSE EXECUTE format('GRANT fet3d_password_recovery_owner TO %I WITH SET %s, INHERIT %s',current_user,CASE WHEN original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN original_inherit THEN 'TRUE' ELSE 'FALSE' END);
    END IF;
  END IF;
END $permissions$;
