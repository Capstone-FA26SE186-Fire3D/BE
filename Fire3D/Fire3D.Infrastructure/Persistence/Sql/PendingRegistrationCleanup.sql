-- Private helper: discover every FK, including composite keys and future business tables.
-- A new unreadable reference fails closed; no historical child is cascaded away.
CREATE FUNCTION public.pending_registration_has_reference(p_target regclass,p_id uuid,p_ignored text[])
RETURNS boolean LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE fk record; predicate text; found boolean;
BEGIN
 IF p_target NOT IN ('public.users'::regclass,'public.organizations'::regclass) THEN
  RAISE EXCEPTION 'invalid maintenance target';
 END IF;
 FOR fk IN SELECT c.* FROM pg_constraint c WHERE c.contype='f' AND c.confrelid=p_target LOOP
  IF fk.conrelid::regclass::text=ANY(p_ignored) THEN CONTINUE; END IF;
  SELECT string_agg(format('child.%I=parent.%I',ca.attname,pa.attname),' AND ')
   INTO predicate FROM unnest(fk.conkey,fk.confkey) AS keys(child_key,parent_key)
   JOIN pg_attribute ca ON ca.attrelid=fk.conrelid AND ca.attnum=keys.child_key
   JOIN pg_attribute pa ON pa.attrelid=fk.confrelid AND pa.attnum=keys.parent_key;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %s child JOIN %s parent ON %s WHERE parent.id=$1)',
   fk.conrelid::regclass,p_target,predicate) INTO found USING p_id;
  IF found THEN RETURN true; END IF;
 END LOOP;
 RETURN false;
END $$;

CREATE FUNCTION public.cleanup_pending_registrations(p_batch integer DEFAULT 100)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,public AS $$
DECLARE candidate record; current_user_row users; removed integer:=0;
BEGIN
 IF p_batch IS NULL OR p_batch NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'invalid cleanup batch'; END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 FOR candidate IN SELECT id FROM users WHERE registration_expires_at<=clock_timestamp()
  AND email_verified_at IS NULL AND deleted_at IS NULL ORDER BY registration_expires_at,id LIMIT p_batch LOOP
  IF NOT pg_try_advisory_xact_lock(hashtextextended('fire3d:auth:'||candidate.id,0)) THEN CONTINUE; END IF;
  IF NOT pg_try_advisory_xact_lock(hashtextextended('fire3d:verification-account:'||candidate.id,0)) THEN CONTINUE; END IF;
  SELECT * INTO current_user_row FROM users WHERE id=candidate.id FOR UPDATE SKIP LOCKED;
  IF current_user_row.id IS NULL OR current_user_row.email_verified_at IS NOT NULL
   OR current_user_row.registration_expires_at IS NULL OR current_user_row.registration_expires_at>clock_timestamp()
   OR current_user_row.deleted_at IS NOT NULL OR current_user_row.avatar_storage_key IS NOT NULL THEN CONTINUE; END IF;
  -- Audit lineage is intentionally not always a foreign key; retain it explicitly.
  IF EXISTS(SELECT 1 FROM audit_logs WHERE user_id=candidate.id
    OR (lower(target_entity) IN('user','users') AND target_id=candidate.id)) THEN CONTINUE; END IF;
  IF public.pending_registration_has_reference('public.users',candidate.id,
   ARRAY['email_verification_tokens','email_verification_jobs','auth_refresh_tokens','user_devices']) THEN CONTINUE; END IF;
  -- Nested transaction rolls back maintenance children if a new FK/reference prevents deletion.
  BEGIN
   DELETE FROM email_verification_tokens WHERE user_id=candidate.id;
   DELETE FROM email_verification_jobs WHERE user_id=candidate.id;
   DELETE FROM auth_refresh_tokens WHERE user_id=candidate.id;
   DELETE FROM user_devices WHERE user_id=candidate.id;
   DELETE FROM users WHERE id=candidate.id;
   IF current_user_row.organization_id IS NOT NULL THEN
    IF EXISTS(SELECT 1 FROM organizations WHERE id=current_user_row.organization_id
     AND registration_owner_user_id=candidate.id FOR UPDATE)
     AND NOT EXISTS(SELECT 1 FROM audit_logs WHERE organization_id=current_user_row.organization_id)
     AND NOT public.pending_registration_has_reference('public.organizations',current_user_row.organization_id,ARRAY[]::text[]) THEN
      DELETE FROM organizations WHERE id=current_user_row.organization_id AND registration_owner_user_id=candidate.id;
    END IF;
   END IF;
   removed:=removed+1;
  EXCEPTION WHEN foreign_key_violation THEN CONTINUE;
  END;
 END LOOP;
 RETURN removed;
END $$;

DO $permissions$
DECLARE os boolean;oi boolean;changed boolean;had_create boolean;t record;r text;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  CREATE ROLE fet3d_pending_cleanup_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
 END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner'
  AND (rolcanlogin OR rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication)) THEN RAISE EXCEPTION 'unsafe cleanup owner'; END IF;
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_pending_cleanup_owner'::regrole AND member=current_user::regrole;
 changed:=NOT pg_has_role(current_user,'fet3d_pending_cleanup_owner','SET') OR NOT pg_has_role(current_user,'fet3d_pending_cleanup_owner','USAGE');
 had_create:=has_schema_privilege('fet3d_pending_cleanup_owner','public','CREATE');
 IF changed THEN EXECUTE format('GRANT fet3d_pending_cleanup_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user); END IF;
 GRANT USAGE,CREATE ON SCHEMA public TO fet3d_pending_cleanup_owner;
 GRANT SELECT,DELETE ON users,organizations,email_verification_tokens,email_verification_jobs,auth_refresh_tokens,user_devices TO fet3d_pending_cleanup_owner;
 GRANT UPDATE(id) ON users,organizations TO fet3d_pending_cleanup_owner;
 FOR t IN SELECT DISTINCT cl.oid,ns.nspname,cl.relname FROM pg_class cl JOIN pg_namespace ns ON ns.oid=cl.relnamespace
  WHERE cl.oid IN ('users'::regclass,'organizations'::regclass,'email_verification_tokens'::regclass,
    'email_verification_jobs'::regclass,'auth_refresh_tokens'::regclass,'user_devices'::regclass,'audit_logs'::regclass)
   OR cl.oid IN(SELECT conrelid FROM pg_constraint WHERE contype='f' AND confrelid IN('users'::regclass,'organizations'::regclass)) LOOP
  EXECUTE format('GRANT USAGE ON SCHEMA %I TO fet3d_pending_cleanup_owner',t.nspname);
  EXECUTE format('GRANT SELECT ON %I.%I TO fet3d_pending_cleanup_owner',t.nspname,t.relname);
  EXECUTE format('CREATE POLICY pending_cleanup_owner ON %I.%I TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true)',t.nspname,t.relname);
 END LOOP;
 ALTER FUNCTION public.pending_registration_has_reference(regclass,uuid,text[]) OWNER TO fet3d_pending_cleanup_owner;
 ALTER FUNCTION public.cleanup_pending_registrations(integer) OWNER TO fet3d_pending_cleanup_owner;
 REVOKE ALL ON FUNCTION public.pending_registration_has_reference(regclass,uuid,text[]),public.cleanup_pending_registrations(integer) FROM PUBLIC;
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   EXECUTE format('REVOKE ALL ON FUNCTION public.cleanup_pending_registrations(integer),public.pending_registration_has_reference(regclass,uuid,text[]) FROM %I',r);
  END IF;
 END LOOP;
 FOREACH r IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   EXECUTE format('GRANT EXECUTE ON FUNCTION public.cleanup_pending_registrations(integer) TO %I',r);
   EXECUTE format('REVOKE DELETE ON users,organizations FROM %I',r);
  END IF;
 END LOOP;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_pending_cleanup_owner; END IF;
 IF changed THEN
  IF os IS NULL THEN EXECUTE format('REVOKE fet3d_pending_cleanup_owner FROM %I',current_user);
  ELSE EXECUTE format('GRANT fet3d_pending_cleanup_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN os THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN oi THEN 'TRUE' ELSE 'FALSE' END); END IF;
 END IF;
END $permissions$;
