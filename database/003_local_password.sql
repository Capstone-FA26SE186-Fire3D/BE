-- Additive migration: local passwords; no existing password/identity is overwritten.
BEGIN;
SET LOCAL lock_timeout = '10s';
ALTER TABLE public.users ADD COLUMN IF NOT EXISTS password_hash text;
ALTER TABLE public.users ALTER COLUMN firebase_uid DROP NOT NULL;
CREATE TABLE IF NOT EXISTS public.local_password_reset_tokens (
 id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
 token_hash text NOT NULL UNIQUE CHECK(token_hash ~ '^[a-f0-9]{64}$'),
 created_at timestamptz NOT NULL DEFAULT now(), expires_at timestamptz NOT NULL,
 used_at timestamptz, CHECK(expires_at>created_at)
);
CREATE INDEX IF NOT EXISTS ix_local_reset_user ON public.local_password_reset_tokens(user_id);
ALTER TABLE public.local_password_reset_tokens ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.local_password_reset_tokens FROM PUBLIC;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_backend_executor') THEN
  GRANT SELECT,INSERT,UPDATE ON public.local_password_reset_tokens TO fet3d_backend_executor;
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='local_password_reset_tokens' AND policyname='local_reset_backend') THEN
   CREATE POLICY local_reset_backend ON public.local_password_reset_tokens TO fet3d_backend_executor USING(true) WITH CHECK(true);
  END IF;
  GRANT SELECT(password_hash),UPDATE(password_hash) ON public.users TO fet3d_backend_executor;
 END IF;
END $$;
DO $$ DECLARE target_table text; BEGIN
 FOREACH target_table IN ARRAY ARRAY['local_password_reset_tokens','password_reset_email_jobs','password_reset_operations'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='anon') THEN
   EXECUTE format('REVOKE ALL ON public.%I FROM anon',target_table);
  END IF;
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='authenticated') THEN
   EXECUTE format('REVOKE ALL ON public.%I FROM authenticated',target_table);
  END IF;
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
   EXECUTE format('GRANT SELECT,INSERT,UPDATE ON public.%I TO fire3d_api',target_table);
   IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=target_table AND policyname='fire3d_api_password_recovery') THEN
    EXECUTE format('CREATE POLICY fire3d_api_password_recovery ON public.%I TO fire3d_api USING(true) WITH CHECK(true)',target_table);
   END IF;
  END IF;
 END LOOP;
END $$;
COMMIT;
