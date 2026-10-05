-- Additive: no user/organization deletion or destructive identity backfill.
CREATE UNIQUE INDEX IF NOT EXISTS users_firebase_uid_key ON public.users(firebase_uid) WHERE firebase_uid IS NOT NULL;
CREATE TABLE public.auth_google_onboarding_sessions (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 firebase_uid varchar(128) NOT NULL CHECK(length(firebase_uid)>0),
 email varchar(255) NOT NULL CHECK(email=lower(btrim(email))),
 onboarding_token_hash varchar(128) NOT NULL UNIQUE CHECK(onboarding_token_hash ~ '^[0-9a-f]{64}$'),
 requested_role public.user_role_enum,
 organization_draft jsonb NOT NULL DEFAULT '{}',
 expires_at timestamptz NOT NULL,
 completed_at timestamptz,
 completed_user_id uuid REFERENCES public.users(id) ON DELETE RESTRICT,
 completed_input_hash varchar(128),
 created_at timestamptz NOT NULL DEFAULT now(),
 CONSTRAINT check_google_onboarding_role CHECK(requested_role IS NULL OR requested_role IN ('Trainee','OrganizationUser')),
 CONSTRAINT check_google_onboarding_dates CHECK(expires_at>created_at),
 CONSTRAINT check_google_onboarding_completion CHECK(completed_at IS NULL OR
  (completed_at>=created_at AND completed_user_id IS NOT NULL AND completed_input_hash ~ '^[0-9a-f]{64}$' AND completed_input_hash IS NOT NULL)),
 CONSTRAINT check_google_onboarding_incomplete CHECK(completed_at IS NOT NULL OR (completed_user_id IS NULL AND completed_input_hash IS NULL))
);
CREATE INDEX idx_google_onboarding_active ON public.auth_google_onboarding_sessions(firebase_uid,expires_at) WHERE completed_at IS NULL;
CREATE INDEX idx_google_onboarding_completed_user ON public.auth_google_onboarding_sessions(completed_user_id) WHERE completed_user_id IS NOT NULL;
ALTER TABLE public.auth_google_onboarding_sessions ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON public.auth_google_onboarding_sessions FROM PUBLIC;
DO $permissions$
DECLARE role_name text;
BEGIN
 FOREACH role_name IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=role_name) THEN
   EXECUTE format('REVOKE ALL ON public.auth_google_onboarding_sessions FROM %I',role_name);
  END IF;
 END LOOP;
 FOREACH role_name IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=role_name) THEN
   EXECUTE format('GRANT SELECT,INSERT,UPDATE ON public.auth_google_onboarding_sessions TO %I',role_name);
   EXECUTE format('CREATE POLICY %I ON public.auth_google_onboarding_sessions TO %I USING(true) WITH CHECK(true)', 'google_onboarding_'||role_name,role_name);
  END IF;
 END LOOP;
END $permissions$;
