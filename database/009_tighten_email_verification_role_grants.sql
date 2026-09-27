BEGIN;

ALTER TABLE public.email_verification_tokens FORCE ROW LEVEL SECURITY;
ALTER TABLE public.email_verification_jobs FORCE ROW LEVEL SECURITY;

DO $$
DECLARE target_role text;
BEGIN
  FOREACH target_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=target_role) THEN
      EXECUTE format('REVOKE ALL PRIVILEGES ON public.email_verification_tokens, public.email_verification_jobs FROM %I', target_role);
    END IF;
  END LOOP;
END $$;

COMMIT;
