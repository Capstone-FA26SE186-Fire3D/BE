-- The deployed runtime login is a member of fire3d_api only. Its baseline grants on the identity, building and authoring
-- tables were made by hand in Supabase and never reached a migration, so a database built from migrations alone left the
-- API unable to register users, issue refresh tokens, write audit rows or read scenarios. These grants cover exactly the
-- direct EF/raw-SQL access of the API process; gate functions keep their own SECURITY DEFINER owners.
-- users/organizations/buildings/auth_refresh_tokens get column-scoped UPDATE so the API can never rewrite role, email,
-- organization_id or ownership columns. buildings INSERT is column-scoped too: SelectedGatePrivileges keeps the access
-- fields (visibility, access_revision, participation_code_hash) writable only through release_access_gate.
-- revisions gets UPDATE(updated_at) only because SELECT ... FOR UPDATE requires an UPDATE privilege.
-- Grants are additive and idempotent; nothing is revoked, no data changes.
DO $$
DECLARE t text; building_columns text; read_only text[]:=ARRAY['release_packages','releases','revision_artifacts','revision_processing_logs',
  'runtime_compatibility_catalog','scenario_drafts','scenario_versions','scenarios','source_documents','validation_runs','revisions'];
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN RETURN;END IF;
 GRANT SELECT,INSERT ON TABLE public.users,public.organizations,public.auth_refresh_tokens,public.audit_logs,
  public.annotation_sets TO fire3d_api;
 SELECT string_agg(quote_ident(attname),',' ORDER BY attnum) INTO building_columns FROM pg_attribute
 WHERE attrelid='public.buildings'::regclass AND attnum>0 AND NOT attisdropped
 AND attname NOT IN('visibility','access_revision','participation_code_hash');
 GRANT SELECT ON TABLE public.buildings TO fire3d_api;
 EXECUTE format('GRANT INSERT (%s) ON TABLE public.buildings TO fire3d_api',building_columns);
 GRANT UPDATE(full_name,username,dob,gender,phone_number,avatar_storage_key,password_hash,firebase_uid,email_verified_at,
  registration_expires_at,last_login_at,is_active,profile_revision,updated_at) ON TABLE public.users TO fire3d_api;
 GRANT UPDATE(name,address,phone,is_active,profile_revision,updated_at) ON TABLE public.organizations TO fire3d_api;
 GRANT UPDATE(name,building_type,total_floors,is_active,updated_at) ON TABLE public.buildings TO fire3d_api;
 GRANT UPDATE(consumed_at,revoked_at) ON TABLE public.auth_refresh_tokens TO fire3d_api;
 GRANT SELECT,INSERT,UPDATE ON TABLE public.user_devices,public.device_installations TO fire3d_api;
 GRANT SELECT ON TABLE public.release_packages,public.releases,public.revision_artifacts,public.revision_processing_logs,
  public.runtime_compatibility_catalog,public.scenario_drafts,public.scenario_versions,public.scenarios,public.source_documents,
  public.validation_runs,public.revisions TO fire3d_api;
 GRANT UPDATE(updated_at) ON TABLE public.revisions TO fire3d_api;
 IF to_regclass('public.fet3d_payos_order_code') IS NOT NULL THEN GRANT USAGE ON SEQUENCE public.fet3d_payos_order_code TO fire3d_api;END IF;
 -- Where RLS is enforced and no fire3d_api policy covers the access, rows would stay invisible despite the grants.
 FOREACH t IN ARRAY ARRAY['users','organizations','buildings','auth_refresh_tokens','audit_logs','annotation_sets','user_devices',
   'device_installations']||read_only LOOP
  IF (SELECT relrowsecurity FROM pg_class WHERE oid=format('public.%I',t)::regclass) AND NOT EXISTS(SELECT 1 FROM pg_policies p
     WHERE p.schemaname='public' AND p.tablename=t AND ('fire3d_api'=ANY(p.roles) OR 'public'=ANY(p.roles))
       AND (p.cmd='ALL' OR (p.cmd='SELECT' AND t=ANY(read_only)))) THEN
   EXECUTE format('DROP POLICY IF EXISTS fire3d_api_runtime ON public.%I',t);
   IF t=ANY(read_only) THEN EXECUTE format('CREATE POLICY fire3d_api_runtime ON public.%I FOR SELECT TO fire3d_api USING (true)',t);
   ELSE EXECUTE format('CREATE POLICY fire3d_api_runtime ON public.%I TO fire3d_api USING (true) WITH CHECK (true)',t);END IF;
  END IF;
 END LOOP;
END $$;
