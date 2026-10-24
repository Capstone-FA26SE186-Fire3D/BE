-- Removes the hand-made Docs v7 trigger functions left on production after AlignProductionSchema dropped their triggers
-- (17/10/2026). Nothing calls them any more: no trigger, no other function, no API code. Eight of them, plus
-- start_training_session, still name tables or the sessions.qr_code_id column dropped by DropUnusedTables, so they would
-- fail if anyone called them. A database built from migrations never had them, so this is a no-op there.
-- A function still used by a trigger is kept (e.g. if a future trigger reuses one); a function still named by another
-- function's body stops the migration instead of being dropped silently.
DO $legacy$
DECLARE f record;
BEGIN
  FOR f IN SELECT p.oid, p.oid::regprocedure AS signature, p.proname FROM pg_proc p
    WHERE p.pronamespace='public'::regnamespace AND p.proname IN (
      'guard_identity_change','validate_qa_run','validate_release_qr_code','validate_release_write','validate_revision_child',
      'validate_session_child','validate_training_session','validate_training_write','start_training_session',
      'update_updated_at_column','deny_snapshot_mutation','validate_owned_content','validate_source',
      'enforce_revision_status_transition','validate_processing_job','validate_scenario_version_write',
      'validate_revision_review_action','apply_revision_review_action','validate_package','verify_result_committed_terminal',
      'record_core_audit')
  LOOP
    IF EXISTS(SELECT 1 FROM pg_trigger t WHERE t.tgfoid=f.oid) THEN
      RAISE NOTICE 'keeping %, a trigger still uses it', f.signature;
      CONTINUE;
    END IF;
    IF EXISTS(SELECT 1 FROM pg_proc q WHERE q.oid<>f.oid AND q.pronamespace='public'::regnamespace
              AND q.prosrc ~ ('\m'||f.proname||'\M')) THEN
      RAISE EXCEPTION 'function % is still referenced by another function', f.signature;
    END IF;
    EXECUTE format('DROP FUNCTION %s RESTRICT', f.signature);
  END LOOP;
END $legacy$;
