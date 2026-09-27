BEGIN;

ALTER TABLE public.users
  ADD COLUMN IF NOT EXISTS registration_expires_at timestamptz NULL;

ALTER TABLE public.email_verification_jobs
  ADD COLUMN IF NOT EXISTS user_id uuid NULL,
  ADD COLUMN IF NOT EXISTS generation integer NOT NULL DEFAULT 1;

UPDATE public.email_verification_jobs j
   SET user_id = u.id
  FROM public.users u
 WHERE j.user_id IS NULL AND j.email = u.email;

UPDATE public.email_verification_jobs SET status='Dead', lease_token=NULL, lease_until=NULL
 WHERE user_id IS NULL AND status IN ('Pending','Leased');

CREATE INDEX IF NOT EXISTS ix_email_verification_jobs_user_generation
  ON public.email_verification_jobs(user_id, generation, created_at);

COMMIT;
