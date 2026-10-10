-- Read-only deployment evidence. Run as the migration/operator identity,
-- never as a reason to grant superuser or delete shared data.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT current_database() AS database_name,current_user AS inspected_as,current_setting('server_version') AS postgres_version;
SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY migration_id DESC LIMIT 8;
SELECT rolname,rolcanlogin,rolsuper,rolbypassrls,rolcreatedb,rolcreaterole,rolreplication
 FROM pg_roles WHERE rolname IN('fire3d_api','fet3d_backend_executor','fet3d_pending_cleanup_owner','fet3d_avatar_cleanup_owner','fet3d_ifc_upload_owner');
SELECT member.rolname AS member,owner.rolname AS inherited_role,m.admin_option,m.inherit_option,m.set_option
 FROM pg_auth_members m JOIN pg_roles member ON member.oid=m.member JOIN pg_roles owner ON owner.oid=m.roleid
 WHERE member.rolname IN('fire3d_api','fet3d_backend_executor','fet3d_pending_cleanup_owner','fet3d_avatar_cleanup_owner','fet3d_ifc_upload_owner')
    OR owner.rolname IN('fet3d_pending_cleanup_owner','fet3d_avatar_cleanup_owner','fet3d_ifc_upload_owner');
SELECT p.oid::regprocedure AS entrypoint,r.rolname AS owner,p.prosecdef,p.proconfig,p.proacl
 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_roles r ON r.oid=p.proowner
 WHERE n.nspname='public' AND p.proname IN('cleanup_pending_registrations','pending_registration_has_reference','avatar_cleanup_gate','scenario_readiness_gate');
SELECT schemaname,tablename,policyname,roles,cmd,qual,with_check FROM pg_policies
 WHERE schemaname='public' AND tablename IN('users','organizations','auth_refresh_tokens','avatar_upload_intents','avatar_object_cleanups');
SELECT c.conrelid::regclass AS referencing_table,c.confrelid::regclass AS referenced_table,c.conname,pg_get_constraintdef(c.oid) AS definition
 FROM pg_constraint c WHERE c.contype='f' AND c.confrelid IN('public.users'::regclass,'public.organizations'::regclass)
 ORDER BY referenced_table,referencing_table,c.conname;
SELECT r.rolname,
 has_table_privilege(r.oid,'public.users','DELETE') AS users_delete_must_be_false,
 has_table_privilege(r.oid,'public.organizations','DELETE') AS organizations_delete_must_be_false,
 has_table_privilege(r.oid,'public.avatar_object_cleanups','INSERT,UPDATE,DELETE') AS queue_dml_must_be_false
 FROM pg_roles r WHERE r.rolname IN('fire3d_api','fet3d_backend_executor');
SELECT r.rolname,has_schema_privilege(r.oid,'public','CREATE') AS schema_create_must_be_false
 FROM pg_roles r WHERE r.rolname IN('fet3d_pending_cleanup_owner','fet3d_avatar_cleanup_owner');
SELECT count(*) FILTER(WHERE email_verified_at IS NULL AND registration_expires_at<=clock_timestamp() AND deleted_at IS NULL) AS expired_legacy_candidates,
 count(*) FILTER(WHERE email_verified_at IS NOT NULL) AS verified_accounts FROM users;
COMMIT;
-- Check custom deployment logins and their inherited/table/column grants too.
-- A missing prerequisite, broad runtime DML, unexpected owner membership or
-- unsafe owner attributes blocks rollout. Migration tests do not replace this.
