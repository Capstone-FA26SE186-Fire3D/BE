-- Removes functions nothing calls any more, found by a production audit on 27/10/2026 (no trigger, no other function body,
-- no API code, no EXECUTE grant to an API role):
-- - five hand-made Docs v7 functions left on production that DropLegacyV7Functions did not list (it covered trigger functions):
--   append_session_event, assert_org_user, complete_training_session, confirm_revision_for_training, lock_content. A database
--   built from migrations never had them.
-- - ai_request_representation: replaced by ai_request_view (AiContractV2); every gate now calls the view.
-- - validate_payment_provisioning_write: the provisioning trigger runs validate_payment_provisioning_purpose_write since
--   BillingV7UpgradeTopUp.
-- apply_verified_payos_webhook is kept: no API role may execute it, but the test suites use it to simulate a paid webhook.
-- A function still used by a trigger is kept; a function still named by another function's body stops the migration instead of
-- being dropped silently. When the migration identity does not own a function, it borrows the owner role only for that drop
-- and restores its membership; a function owned by a superuser it cannot borrow is kept with a notice.
DO $dead$
DECLARE f record;original_set boolean;original_inherit boolean;borrowed boolean;
BEGIN
  FOR f IN SELECT p.oid, p.oid::regprocedure AS signature, p.proname, p.proowner, r.rolname AS owner, r.rolsuper AS owner_super
    FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner
    WHERE p.pronamespace='public'::regnamespace AND p.proname IN (
      'append_session_event','assert_org_user','complete_training_session','confirm_revision_for_training','lock_content',
      'ai_request_representation','validate_payment_provisioning_write')
  LOOP
    IF EXISTS(SELECT 1 FROM pg_trigger t WHERE t.tgfoid=f.oid) THEN
      RAISE NOTICE 'keeping %, a trigger still uses it', f.signature;
      CONTINUE;
    END IF;
    IF EXISTS(SELECT 1 FROM pg_proc q WHERE q.oid<>f.oid AND q.pronamespace='public'::regnamespace
              AND q.prosrc ~ ('\m'||f.proname||'\M')) THEN
      RAISE EXCEPTION 'function % is still referenced by another function', f.signature;
    END IF;
    borrowed:=false;
    IF NOT pg_has_role(current_user,f.proowner,'USAGE') THEN
      IF f.owner_super THEN
        RAISE NOTICE 'keeping %, owned by superuser % which this migration cannot borrow', f.signature, f.owner;
        CONTINUE;
      END IF;
      SELECT set_option,inherit_option INTO original_set,original_inherit FROM pg_auth_members WHERE roleid=f.proowner AND member=current_user::regrole;
      EXECUTE format('GRANT %I TO %I WITH INHERIT TRUE',f.owner,current_user);
      borrowed:=true;
    END IF;
    EXECUTE format('DROP FUNCTION %s RESTRICT', f.signature);
    IF borrowed THEN
      IF original_set IS NULL THEN EXECUTE format('REVOKE %I FROM %I',f.owner,current_user);
      ELSE EXECUTE format('GRANT %I TO %I WITH SET %s,INHERIT %s',f.owner,current_user,
        CASE WHEN original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN original_inherit THEN 'TRUE' ELSE 'FALSE' END);
      END IF;
    END IF;
  END LOOP;
END $dead$;
