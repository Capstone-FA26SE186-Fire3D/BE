-- Password recovery tables were only created by the hand-run database/002 and 003 scripts, so a database built from
-- migrations alone broke forgot/reset/change password and Google link/set-password. Definitions match those scripts;
-- every statement is idempotent so databases that already ran them (Supabase) keep their rows and only gain grants.
SET LOCAL lock_timeout='10s';
CREATE TABLE IF NOT EXISTS public.password_reset_email_jobs (
 id uuid PRIMARY KEY, email varchar(254) NOT NULL,
 status text NOT NULL DEFAULT 'Pending' CHECK(status IN ('Pending','Leased','Sent','Dead')),
 attempts int NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 5),
 available_at timestamptz NOT NULL DEFAULT now(), created_at timestamptz NOT NULL DEFAULT now(),
 lease_token uuid, lease_until timestamptz,
 CHECK ((status='Leased' AND lease_token IS NOT NULL AND lease_until IS NOT NULL)
     OR (status<>'Leased' AND lease_token IS NULL AND lease_until IS NULL))
);
CREATE INDEX IF NOT EXISTS ix_reset_email_dispatch ON public.password_reset_email_jobs(status,available_at,lease_until);
CREATE INDEX IF NOT EXISTS ix_reset_email_dedup ON public.password_reset_email_jobs(email,created_at);
CREATE TABLE IF NOT EXISTS public.password_reset_operations (
 id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES public.users(id), firebase_uid text NOT NULL,
 code_hash char(64) NOT NULL UNIQUE CHECK(code_hash ~ '^[a-f0-9]{64}$'),
 status text NOT NULL CHECK(status IN ('Pending','Completed','Rejected')),
 created_at timestamptz NOT NULL DEFAULT now(), finished_at timestamptz,
 CHECK ((status='Pending') = (finished_at IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_one_pending_reset ON public.password_reset_operations(user_id) WHERE status='Pending';
CREATE INDEX IF NOT EXISTS ix_reset_session_fence ON public.password_reset_operations(user_id,status,finished_at);
CREATE TABLE IF NOT EXISTS public.local_password_reset_tokens (
 id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
 token_hash text NOT NULL UNIQUE CHECK(token_hash ~ '^[a-f0-9]{64}$'),
 created_at timestamptz NOT NULL DEFAULT now(), expires_at timestamptz NOT NULL,
 used_at timestamptz, CHECK(expires_at>created_at)
);
CREATE INDEX IF NOT EXISTS ix_local_reset_user ON public.local_password_reset_tokens(user_id);
DO $$
DECLARE t text; grantee text;
BEGIN
 FOREACH t IN ARRAY ARRAY['local_password_reset_tokens','password_reset_email_jobs','password_reset_operations'] LOOP
  EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('REVOKE ALL ON public.%I FROM PUBLIC',t);
  -- Supabase API roles must never reach recovery secrets.
  FOREACH grantee IN ARRAY ARRAY['anon','authenticated'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=grantee) THEN EXECUTE format('REVOKE ALL ON public.%I FROM %I',t,grantee);END IF;
  END LOOP;
  -- Runtime paths only insert, read and mark rows; none deletes them.
  FOREACH grantee IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=grantee) THEN
    EXECUTE format('GRANT SELECT,INSERT,UPDATE ON public.%I TO %I',t,grantee);
    IF NOT EXISTS(SELECT 1 FROM pg_policies p WHERE p.schemaname='public' AND p.tablename=t AND grantee=ANY(p.roles::text[])) THEN
     EXECUTE format('CREATE POLICY %I ON public.%I TO %I USING(true) WITH CHECK(true)',grantee||'_password_recovery',t,grantee);
    END IF;
   END IF;
  END LOOP;
 END LOOP;
END $$;
