-- Drops revision_processing_logs and the two enum types only it used (processing_step_enum, processing_step_status_enum).
-- The table never had a writer: no API code, gate, trigger or worker inserts into it, and only superusers hold INSERT on
-- production. GET /api/revisions/{id}/processing-logs reads processing_job_attempts since DropDeadFunctions' change, which must
-- be deployed before this migration is applied. Refuses to run while the table holds rows; safe to rerun. When the migration
-- identity does not own an object it borrows a non-superuser owner role only for the drop; an object owned by a superuser it
-- cannot borrow is kept with a notice.
DO $drop$
DECLARE o record;original_set boolean;original_inherit boolean;borrowed boolean;table_kept boolean:=false;
BEGIN
 IF to_regclass('public.revision_processing_logs') IS NOT NULL AND EXISTS(SELECT 1 FROM public.revision_processing_logs) THEN
  RAISE EXCEPTION 'revision_processing_logs holds rows; review them before dropping the table';
 END IF;
 FOR o IN
  SELECT 'TABLE' kind,format('public.%I',c.relname) name,c.relowner owner FROM pg_class c
   WHERE c.oid=to_regclass('public.revision_processing_logs')
  UNION ALL
  SELECT 'TYPE',format('public.%I',t.typname),t.typowner FROM pg_type t
   WHERE t.typnamespace='public'::regnamespace AND t.typname IN('processing_step_enum','processing_step_status_enum')
  ORDER BY 1
 LOOP
  -- A kept table still uses the types.
  IF o.kind='TYPE' AND table_kept THEN RAISE NOTICE 'keeping %, the table that uses it was kept', o.name;CONTINUE;END IF;
  borrowed:=false;
  IF NOT pg_has_role(current_user,o.owner,'USAGE') THEN
   IF (SELECT rolsuper FROM pg_roles WHERE oid=o.owner) THEN
    RAISE NOTICE 'keeping %, owned by a superuser this migration cannot borrow', o.name;
    table_kept:=table_kept OR o.kind='TABLE';
    CONTINUE;
   END IF;
   SELECT set_option,inherit_option INTO original_set,original_inherit FROM pg_auth_members WHERE roleid=o.owner AND member=current_user::regrole;
   EXECUTE format('GRANT %I TO %I WITH INHERIT TRUE',(SELECT rolname FROM pg_roles WHERE oid=o.owner),current_user);
   borrowed:=true;
  END IF;
  -- The table goes first (ORDER BY kind), so the types have no dependent column left; RESTRICT stops on anything unexpected.
  EXECUTE format('DROP %s %s RESTRICT',o.kind,o.name);
  IF borrowed THEN
   IF original_set IS NULL THEN EXECUTE format('REVOKE %I FROM %I',(SELECT rolname FROM pg_roles WHERE oid=o.owner),current_user);
   ELSE EXECUTE format('GRANT %I TO %I WITH SET %s,INHERIT %s',(SELECT rolname FROM pg_roles WHERE oid=o.owner),current_user,
    CASE WHEN original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN original_inherit THEN 'TRUE' ELSE 'FALSE' END);
   END IF;
  END IF;
 END LOOP;
END $drop$;
