-- The backend never uses Supabase's client roles: browsers and apps reach the database only through the API gates. Some tables
-- created earlier (playtest_sessions, registration_email_challenges, registration_otp_email_jobs, runtime_compatibility_catalog,
-- scenario_drafts) still carried Supabase default grants (ALL privileges, including TRUNCATE, which RLS does not cover) for anon and
-- authenticated, and one sequence was usable by them. RLS kept the rows hidden from the REST API; this removes the grants themselves.
-- Only relations where a client role actually holds a privilege are touched (REVOKE on an object the migration role has no
-- privilege on is an error); one the migration role cannot manage is reported instead of failing the migration.
DO $$ DECLARE r text;o record;BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   FOR o IN SELECT c.relname,c.relkind,c.relowner FROM pg_class c WHERE c.relnamespace='public'::regnamespace AND c.relkind IN('r','p','v','m','f','S')
     AND EXISTS(SELECT 1 FROM aclexplode(c.relacl) a WHERE a.grantee=r::regrole) LOOP
    IF NOT pg_has_role(current_user,o.relowner,'USAGE') THEN
     RAISE NOTICE 'client role % keeps a grant on %, owned by another role', r, o.relname;
    ELSIF o.relkind='S' THEN EXECUTE format('REVOKE ALL ON SEQUENCE public.%I FROM %I',o.relname,r);
    ELSE EXECUTE format('REVOKE ALL ON TABLE public.%I FROM %I',o.relname,r);END IF;
   END LOOP;
   -- Tables the migration role creates later get no client grants either.
   EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM %I',r);
   EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM %I',r);
  END IF;
 END LOOP;
END $$;
