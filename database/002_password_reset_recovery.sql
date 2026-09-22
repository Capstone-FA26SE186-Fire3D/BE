-- Apply once before deploying password-reset code. No existing data is removed.
BEGIN;
CREATE TABLE public.password_reset_email_jobs (
 id uuid PRIMARY KEY, email varchar(254) NOT NULL,
 status text NOT NULL DEFAULT 'Pending' CHECK(status IN ('Pending','Leased','Sent','Dead')),
 attempts int NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 5),
 available_at timestamptz NOT NULL DEFAULT now(), created_at timestamptz NOT NULL DEFAULT now(),
 lease_token uuid, lease_until timestamptz,
 CHECK ((status='Leased' AND lease_token IS NOT NULL AND lease_until IS NOT NULL)
     OR (status<>'Leased' AND lease_token IS NULL AND lease_until IS NULL))
);
CREATE INDEX ix_reset_email_dispatch ON public.password_reset_email_jobs(status,available_at,lease_until);
CREATE INDEX ix_reset_email_dedup ON public.password_reset_email_jobs(email,created_at);
CREATE TABLE public.password_reset_operations (
 id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES public.users(id), firebase_uid text NOT NULL,
 code_hash char(64) NOT NULL UNIQUE CHECK(code_hash ~ '^[a-f0-9]{64}$'),
 status text NOT NULL CHECK(status IN ('Pending','Completed','Rejected')),
 created_at timestamptz NOT NULL DEFAULT now(), finished_at timestamptz,
 CHECK ((status='Pending') = (finished_at IS NULL))
);
CREATE UNIQUE INDEX ix_one_pending_reset ON public.password_reset_operations(user_id) WHERE status='Pending';
CREATE INDEX ix_reset_session_fence ON public.password_reset_operations(user_id,status,finished_at);
ALTER TABLE public.password_reset_email_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.password_reset_operations ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.password_reset_email_jobs, public.password_reset_operations FROM PUBLIC;
-- Only the trusted server DB role may access these tables; never grant anon/authenticated access.
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fet3d_backend_executor') THEN
  GRANT SELECT,INSERT,UPDATE ON public.password_reset_email_jobs,public.password_reset_operations TO fet3d_backend_executor;
  CREATE POLICY reset_jobs_backend ON public.password_reset_email_jobs TO fet3d_backend_executor USING (true) WITH CHECK (true);
  CREATE POLICY reset_operations_backend ON public.password_reset_operations TO fet3d_backend_executor USING (true) WITH CHECK (true);
 END IF;
END $$;
COMMIT;
