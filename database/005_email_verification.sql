BEGIN;

ALTER TABLE public.users ADD COLUMN IF NOT EXISTS email_verified_at timestamptz;

CREATE TABLE IF NOT EXISTS public.email_verification_tokens (
  id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
  token_hash char(64) NOT NULL UNIQUE, created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL, used_at timestamptz NULL
);
CREATE INDEX IF NOT EXISTS ix_email_verification_tokens_user_id ON public.email_verification_tokens(user_id);

CREATE TABLE IF NOT EXISTS public.email_verification_jobs (
  id uuid PRIMARY KEY, email text NOT NULL, status text NOT NULL DEFAULT 'Pending', attempts integer NOT NULL DEFAULT 0,
  available_at timestamptz NOT NULL DEFAULT now(), lease_token uuid NULL, lease_until timestamptz NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_email_verification_jobs_claim ON public.email_verification_jobs(status, available_at, created_at);

COMMIT;
