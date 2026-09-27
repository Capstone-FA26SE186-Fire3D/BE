BEGIN;

ALTER TABLE public.organizations
  ADD COLUMN IF NOT EXISTS registration_owner_user_id uuid NULL;

CREATE INDEX IF NOT EXISTS ix_organizations_registration_owner_user_id
  ON public.organizations(registration_owner_user_id)
  WHERE registration_owner_user_id IS NOT NULL;

COMMIT;
