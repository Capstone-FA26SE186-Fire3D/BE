-- Supabase's hand-made baseline gave fire3d_api table-wide UPDATE on users, organizations and auth_refresh_tokens, so the
-- API login could rewrite users.role, email or organization_id. RuntimeCoreGrants only adds grants and could not narrow it.
-- Revoking the table-level privilege also drops column privileges of the same kind, so the reviewed column lists are
-- re-granted immediately. No API path deletes buildings; DELETE/TRUNCATE held by hand-made logins is removed as well.
DO $$
DECLARE grantee_row record; recipient text;
BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
  REVOKE UPDATE ON TABLE public.users,public.organizations,public.auth_refresh_tokens FROM fire3d_api;
  GRANT UPDATE(full_name,username,dob,gender,phone_number,avatar_storage_key,password_hash,firebase_uid,email_verified_at,
   registration_expires_at,last_login_at,is_active,profile_revision,updated_at) ON TABLE public.users TO fire3d_api;
  GRANT UPDATE(name,address,phone,is_active,profile_revision,updated_at) ON TABLE public.organizations TO fire3d_api;
  GRANT UPDATE(consumed_at,revoked_at) ON TABLE public.auth_refresh_tokens TO fire3d_api;
 END IF;
 FOR grantee_row IN SELECT DISTINCT a.grantee FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
  LEFT JOIN pg_roles r ON r.oid=a.grantee WHERE c.oid='public.buildings'::regclass AND a.privilege_type IN('DELETE','TRUNCATE')
  AND a.grantee<>c.relowner AND NOT COALESCE(r.rolsuper,false) LOOP
  recipient:=CASE WHEN grantee_row.grantee=0 THEN 'PUBLIC' ELSE quote_ident(grantee_row.grantee::regrole::text) END;
  EXECUTE format('REVOKE DELETE,TRUNCATE ON TABLE public.buildings FROM %s',recipient);
 END LOOP;
END $$;
