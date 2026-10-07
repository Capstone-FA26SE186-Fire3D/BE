-- Read only. Run using the intended migration identity before deployment.
-- No event payload, phone/email, credentials or runtime data is returned.
SELECT current_user AS migration_identity,
 has_schema_privilege(current_user,'public','USAGE') AS schema_usage,
 has_schema_privilege(current_user,'public','CREATE WITH GRANT OPTION') AS can_grant_temporary_schema_create,
 pg_has_role(current_user,'fet3d_ifc_upload_owner','SET') AS owner_set_before,
 pg_has_role(current_user,'fet3d_ifc_upload_owner','USAGE') AS owner_inherit_before;

SELECT c.relname,
 pg_get_userbyid(c.relowner) AS table_owner,
 pg_has_role(current_user,c.relowner,'USAGE') AS owns_via_effective_membership,
 has_table_privilege(current_user,c.oid,'REFERENCES') AS can_reference,
 has_table_privilege(current_user,c.oid,'SELECT') AS can_read
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname='public' AND c.relname IN
 ('integration_outbox_events','integration_event_consumptions','processing_delivery_receipts')
ORDER BY c.relname;

SELECT rolname,rolcanlogin,rolsuper,rolbypassrls,rolcreatedb,rolcreaterole,rolreplication
FROM pg_roles WHERE rolname IN('fet3d_ifc_upload_owner','fet3d_dispatcher_executor','fet3d_processing_executor')
ORDER BY rolname;
