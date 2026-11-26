-- BE#237: production still had users_check, the role/organization CHECK of the hand-made Supabase baseline
-- (OrganizationUser with an organization; PlatformAdmin and Trainee without one). check_user_role_organization
-- (ExpertContentReview.sql, BE#211) added FireSafetyExpert but the old rule was never dropped, so every FireSafetyExpert
-- insert failed with 23514 and POST /api/accounts answered 500. Databases built from migrations never had users_check.
-- It is dropped only when it is that role/organization rule; check_user_role_organization stays the single rule.
DO $$
DECLARE definition text;
BEGIN
 SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
  WHERE conrelid='public.users'::regclass AND conname='users_check' AND contype='c';
 IF definition IS NOT NULL AND definition ~ 'role' AND definition ~ 'organization_id' AND definition !~ 'FireSafetyExpert' THEN
  ALTER TABLE public.users DROP CONSTRAINT users_check;
 END IF;
END $$;
