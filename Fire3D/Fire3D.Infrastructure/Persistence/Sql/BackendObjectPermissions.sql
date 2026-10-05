-- Supabase grants new public objects to its REST roles by default. These objects
-- belong to the trusted backend, not the Supabase client-facing API.
DO $hardening$
DECLARE target text; client_role text; backend_role text; signature text;
BEGIN
  FOREACH target IN ARRAY ARRAY['__EFMigrationsHistory','avatar_object_cleanups',
    'avatar_upload_intents','device_installations','password_reset_tokens','support_ticket_messages'] LOOP
    IF to_regclass(format('public.%I',target)) IS NULL THEN CONTINUE; END IF;
    EXECUTE format('REVOKE ALL ON TABLE public.%I FROM PUBLIC',target);
    FOREACH client_role IN ARRAY ARRAY['anon','authenticated'] LOOP
      IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client_role) THEN
        EXECUTE format('REVOKE ALL ON TABLE public.%I FROM %I',target,client_role);
      END IF;
    END LOOP;
    EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',target);
    -- Preserve existing table grants. RLS additionally restricts any access to
    -- the known backend roles; migration owners keep their normal owner access.
    FOREACH backend_role IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
      IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=backend_role) THEN
        EXECUTE format('DROP POLICY IF EXISTS %I ON public.%I','backend_only_'||backend_role,target);
        EXECUTE format('CREATE POLICY %I ON public.%I TO %I USING (true) WITH CHECK (true)',
          'backend_only_'||backend_role,target,backend_role);
      END IF;
    END LOOP;
  END LOOP;
  FOR signature IN SELECT p.oid::regprocedure::text FROM pg_proc p
    WHERE p.pronamespace='public'::regnamespace AND p.proname IN (
      'apply_verified_payos_webhook','bind_payos_checkout','claim_payos_provisioning_context',
      'create_pending_payos_payment_request','finalize_payos_provisioning',
      'mark_payos_navigation_state','process_payos_inbox') LOOP
    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
    FOREACH client_role IN ARRAY ARRAY['anon','authenticated'] LOOP
      IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client_role) THEN
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',signature,client_role);
      END IF;
    END LOOP;
  END LOOP;
  -- Only change defaults for this migration owner, never for Supabase-managed
  -- owners. Existing objects/executor grants are deliberately not swept away.
  FOREACH client_role IN ARRAY ARRAY['anon','authenticated'] LOOP
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client_role) THEN
      EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM %I',client_role);
      EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM %I',client_role);
      EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM %I',client_role);
    END IF;
  END LOOP;
END $hardening$;
-- Function EXECUTE granted to PUBLIC is a global default; a schema-local
-- revoke alone cannot override it. New functions must explicitly grant callers.
ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
