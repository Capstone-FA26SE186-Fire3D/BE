-- Align the identity tables with Docs schema v7 without deleting or rewriting rows.
-- RENAME COLUMN keeps data, defaults and column privileges. Business CHECKs that existing rows might violate are only
-- added when no row violates them; otherwise a NOTICE names the constraint so the data can be reviewed first.
SET LOCAL lock_timeout='10s';
DO $$
DECLARE t text; violations bigint;
BEGIN
 FOREACH t IN ARRAY ARRAY['users','organizations'] LOOP
  IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=t AND column_name='profile_revision') THEN
   EXECUTE format('ALTER TABLE public.%I RENAME COLUMN profile_revision TO profile_revision_no',t);
  END IF;
 END LOOP;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='public.users'::regclass AND conname='check_user_profile_revision') THEN
  ALTER TABLE public.users ADD CONSTRAINT check_user_profile_revision CHECK (profile_revision_no > 0);
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='public.organizations'::regclass AND conname='check_organization_profile_revision') THEN
  ALTER TABLE public.organizations ADD CONSTRAINT check_organization_profile_revision CHECK (profile_revision_no > 0);
 END IF;

 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='public.users'::regclass AND conname='check_user_role_organization') THEN
  SELECT count(*) INTO violations FROM public.users WHERE NOT (
   (role='OrganizationUser' AND organization_id IS NOT NULL) OR (role IN ('PlatformAdmin','Trainee') AND organization_id IS NULL));
  IF violations=0 THEN
   ALTER TABLE public.users ADD CONSTRAINT check_user_role_organization CHECK (
    (role='OrganizationUser' AND organization_id IS NOT NULL) OR (role IN ('PlatformAdmin','Trainee') AND organization_id IS NULL));
  ELSE RAISE NOTICE 'check_user_role_organization skipped: % users violate it', violations;
  END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='public.users'::regclass AND conname='check_user_auth_method') THEN
  SELECT count(*) INTO violations FROM public.users WHERE password_hash IS NULL AND firebase_uid IS NULL;
  IF violations=0 THEN
   ALTER TABLE public.users ADD CONSTRAINT check_user_auth_method CHECK (password_hash IS NOT NULL OR firebase_uid IS NOT NULL);
  ELSE RAISE NOTICE 'check_user_auth_method skipped: % users violate it', violations;
  END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='public.users'::regclass AND conname='check_trainee_username_required') THEN
  SELECT count(*) INTO violations FROM public.users WHERE role='Trainee' AND username IS NULL;
  IF violations=0 THEN
   ALTER TABLE public.users ADD CONSTRAINT check_trainee_username_required CHECK (role <> 'Trainee' OR username IS NOT NULL);
  ELSE RAISE NOTICE 'check_trainee_username_required skipped: % legacy trainees have no username', violations;
  END IF;
 END IF;
END $$;

-- user_devices: v7 column types/names. device_uuid stays unique per account until existing rows are reviewed.
ALTER TABLE public.user_devices ALTER COLUMN fcm_token TYPE text;
ALTER TABLE public.user_devices ADD COLUMN IF NOT EXISTS fcm_token_updated_at timestamptz;
ALTER INDEX IF EXISTS public.ux_user_devices_active_fcm_token RENAME TO device_active_fcm;
ALTER TABLE public.user_devices DROP CONSTRAINT IF EXISTS user_devices_user_id_fkey,
 ADD CONSTRAINT user_devices_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE RESTRICT;
