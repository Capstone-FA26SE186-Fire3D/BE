-- Drops tables that no API code, SQL gate or worker reads or writes any more (audit 2026-10-24, all empty on production):
--   release_qr_codes, revision_floors, building_floors  (QR join now goes through building_qr_codes; floors come from revision artifacts)
--   session_checkpoints, debrief_artifacts             (learner state and results live in sessions/session_events/session_results)
--   invoice_metadata                                   (never written by the PayOS flow)
--   password_reset_tokens, password_reset_operations   (Firebase-era reset; local_password_reset_tokens replaced them)
--   auth_password_reset_tokens                         (created outside migrations on production only)
-- Refuses to run if any of them still holds rows, so no data is lost silently.
DO $guard$
DECLARE t text; n bigint;
BEGIN
  FOREACH t IN ARRAY ARRAY['release_qr_codes','revision_floors','building_floors','session_checkpoints','debrief_artifacts',
    'invoice_metadata','password_reset_tokens','password_reset_operations','auth_password_reset_tokens'] LOOP
    IF to_regclass('public.'||t) IS NOT NULL THEN
      EXECUTE format('SELECT count(*) FROM public.%I',t) INTO n;
      IF n>0 THEN RAISE EXCEPTION 'table % still has % rows; migrate them before dropping', t, n; END IF;
    END IF;
  END LOOP;
  IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='sessions' AND column_name='qr_code_id') THEN
    EXECUTE 'SELECT count(*) FROM public.sessions WHERE qr_code_id IS NOT NULL' INTO n;
    IF n>0 THEN RAISE EXCEPTION 'sessions.qr_code_id is still set on % rows', n; END IF;
  END IF;
END $guard$;

-- The legacy invalidation function is owned by the restricted recovery owner; borrow membership only for the drop.
DO $legacy$
DECLARE original_set boolean; original_inherit boolean; changed_membership boolean;
BEGIN
  IF to_regprocedure('public.invalidate_legacy_reset_tokens(uuid)') IS NULL THEN RETURN; END IF;
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_password_recovery_owner') THEN
    SELECT set_option,inherit_option INTO original_set,original_inherit FROM pg_auth_members
      WHERE roleid='fet3d_password_recovery_owner'::regrole AND member=current_user::regrole;
    changed_membership:=NOT pg_has_role(current_user,'fet3d_password_recovery_owner','USAGE');
    IF changed_membership THEN EXECUTE format('GRANT fet3d_password_recovery_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user); END IF;
    DROP FUNCTION public.invalidate_legacy_reset_tokens(uuid);
    IF changed_membership THEN
      IF original_set IS NULL THEN EXECUTE format('REVOKE fet3d_password_recovery_owner FROM %I',current_user);
      ELSE EXECUTE format('GRANT fet3d_password_recovery_owner TO %I WITH SET %s, INHERIT %s',current_user,CASE WHEN original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN original_inherit THEN 'TRUE' ELSE 'FALSE' END);
      END IF;
    END IF;
  ELSE
    DROP FUNCTION public.invalidate_legacy_reset_tokens(uuid);
  END IF;
END $legacy$;

ALTER TABLE public.sessions DROP CONSTRAINT IF EXISTS sessions_qr_code_id_training_id_release_id_organization_id_fkey;
ALTER TABLE public.sessions DROP COLUMN IF EXISTS qr_code_id;
-- RESTRICT: fail instead of cascading into anything this audit did not see.
DROP TABLE IF EXISTS public.release_qr_codes, public.revision_floors, public.building_floors,
  public.session_checkpoints, public.debrief_artifacts, public.invoice_metadata,
  public.password_reset_tokens, public.password_reset_operations, public.auth_password_reset_tokens RESTRICT;
