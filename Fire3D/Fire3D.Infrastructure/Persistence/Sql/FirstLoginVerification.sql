-- BE#235: a FireSafetyExpert account created by PlatformAdmin proves its email on the first sign-in and then changes the initial password.
-- * users.first_login_step: NULL (normal), 'VerifyEmail' (no session until a code sent to the email, or a Google identity of that email,
--   is presented after the correct password) or 'ChangePassword' (a session that may only change the password, read /api/auth/me and log
--   out). Existing accounts stay NULL, so nobody is locked out by this migration.
-- * first_login_verifications: one row per account waiting in VerifyEmail. The challenge token (returned by the password login) and the
--   6-digit code are stored as hashes only; the code expires after 10 minutes and 5 wrong attempts lock it until a new one is sent.
-- * first_login_email_jobs: durable delivery of the code; the job keeps the code encrypted with a key derived from the JWT signing key and
--   is sent only while it is the current code of the account.
-- * users.email: fire3d_api may now update it, but the trigger users_email_only_while_unverified refuses any change of an account that is
--   not in VerifyEmail, so a verified email can still never be rewritten by the API (PlatformAdmin fixes a mistyped address only).
--   The owner of users and superusers (migrations, reviewed data fixes) are not limited.
ALTER TABLE public.users ADD COLUMN IF NOT EXISTS first_login_step text;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_user_first_login_step') THEN
  ALTER TABLE public.users ADD CONSTRAINT check_user_first_login_step CHECK(first_login_step IS NULL OR first_login_step IN('VerifyEmail','ChangePassword'));
 END IF;
END $$;

CREATE OR REPLACE FUNCTION public.users_email_only_while_unverified() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.email IS DISTINCT FROM OLD.email AND OLD.first_login_step IS DISTINCT FROM 'VerifyEmail'
    AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=current_user AND rolsuper)
    AND NOT pg_catalog.pg_has_role(current_user,(SELECT relowner FROM pg_catalog.pg_class WHERE oid='public.users'::pg_catalog.regclass),'USAGE') THEN
  RAISE EXCEPTION 'users.email can change only while the account has not verified it (first_login_step VerifyEmail)' USING ERRCODE='check_violation';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS users_email_only_while_unverified ON public.users;
CREATE TRIGGER users_email_only_while_unverified BEFORE UPDATE OF email ON public.users
 FOR EACH ROW EXECUTE FUNCTION public.users_email_only_while_unverified();

CREATE TABLE IF NOT EXISTS public.first_login_verifications(
 user_id uuid PRIMARY KEY REFERENCES public.users(id) ON DELETE CASCADE,
 challenge_token_hash varchar(64) NOT NULL CHECK(challenge_token_hash ~ '^[0-9a-f]{64}$'),
 challenge_expires_at timestamptz NOT NULL,
 otp_hash varchar(64) NOT NULL CHECK(otp_hash ~ '^[0-9a-f]{64}$'),
 otp_version integer NOT NULL CHECK(otp_version>0),
 otp_expires_at timestamptz NOT NULL,
 failed_attempts integer NOT NULL DEFAULT 0 CHECK(failed_attempts BETWEEN 0 AND 5),
 last_sent_at timestamptz NOT NULL,
 send_window_started_at timestamptz NOT NULL,
 sends_in_window integer NOT NULL CHECK(sends_in_window BETWEEN 1 AND 5),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now());
CREATE UNIQUE INDEX IF NOT EXISTS first_login_verifications_token ON public.first_login_verifications(challenge_token_hash);

CREATE TABLE IF NOT EXISTS public.first_login_email_jobs(
 id uuid PRIMARY KEY,
 user_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
 otp_version integer NOT NULL,
 email text NOT NULL,
 otp_ciphertext bytea NOT NULL,
 otp_nonce bytea NOT NULL,
 otp_tag bytea NOT NULL,
 status text NOT NULL DEFAULT 'Pending' CHECK(status IN('Pending','Leased','Succeeded','Dead')),
 attempts integer NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 5),
 available_at timestamptz NOT NULL DEFAULT now(),
 lease_token uuid,
 lease_until timestamptz,
 created_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX IF NOT EXISTS first_login_email_jobs_pending ON public.first_login_email_jobs(available_at) WHERE status IN('Pending','Leased');

ALTER TABLE public.first_login_verifications ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.first_login_email_jobs ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.first_login_verifications, public.first_login_email_jobs FROM PUBLIC;
DO $$
DECLARE target_role text; target_table text; policy_name text;
BEGIN
 FOREACH target_table IN ARRAY ARRAY['first_login_verifications','first_login_email_jobs'] LOOP
  FOREACH target_role IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=target_role) THEN
    EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON public.%I TO %I', target_table, target_role);
    policy_name:=format('first_login_%s', replace(target_role,'_',''));
    IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=target_table AND policyname=policy_name) THEN
     EXECUTE format('CREATE POLICY %I ON public.%I TO %I USING(true) WITH CHECK(true)', policy_name, target_table, target_role);
    END IF;
   END IF;
  END LOOP;
 END LOOP;
 FOREACH target_role IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=target_role) THEN
   EXECUTE format('GRANT UPDATE(first_login_step,email) ON TABLE public.users TO %I', target_role);
  END IF;
 END LOOP;
 -- cleanup_pending_registrations (owner fet3d_pending_cleanup_owner) deletes users; the ON DELETE CASCADE of these tables needs the same
 -- access it has on every other table that references users (PendingRegistrationCleanup.sql).
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  FOREACH target_table IN ARRAY ARRAY['first_login_verifications','first_login_email_jobs'] LOOP
   EXECUTE format('GRANT SELECT,DELETE ON public.%I TO fet3d_pending_cleanup_owner', target_table);
   IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=target_table AND policyname='pending_cleanup_owner') THEN
    EXECUTE format('CREATE POLICY pending_cleanup_owner ON public.%I TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true)', target_table);
   END IF;
  END LOOP;
 END IF;
 FOREACH target_role IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=target_role) THEN
   EXECUTE format('REVOKE ALL ON public.first_login_verifications, public.first_login_email_jobs FROM %I', target_role);
  END IF;
 END LOOP;
END $$;
