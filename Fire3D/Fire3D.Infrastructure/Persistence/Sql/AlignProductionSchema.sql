-- Production drift found on 17/10/2026: the Supabase database still carries 73 triggers created by hand from the Docs v7 SQL
-- scripts, not by any migration, so neither a migrated database nor the test suite has them. One of them (core_audit ->
-- record_core_audit) inserts audit_logs rows without an id, whose default extensions.gen_random_uuid() needs USAGE on schema
-- extensions that no application role has: every Building insert by the API failed with 500 (last Building created 03/10), and the
-- revision/scenario/session/release paths hit the same triggers plus validate_*/owned_* checks that contradict the gates (for
-- example a PlatformAdmin-created Building or revision, NeedsFix transitions, rejected events keeping their sequence, ETag
-- updated_at being overwritten). Production has no revision, scenario, session or release yet, so no data depends on them.
-- This migration makes the database match the migrated, tested schema:
-- 1. Drops exactly those (table, trigger, function) triggers when present; trigger functions stay, they are no longer called.
--    Triggers the migrations create (some reuse the same functions under other names, e.g. PayOS) are untouched.
-- 2. Grants USAGE on schema extensions to the application roles, so column defaults such as extensions.gen_random_uuid() work.
-- On a database built from migrations both steps are no-ops apart from the grant.
DO $$
DECLARE pair record;rel regclass;owner name;granted name[]:='{}';r name;
BEGIN
 FOR pair IN SELECT * FROM (VALUES
 ('annotation_sets','annotation_insert','validate_revision_child'),
 ('annotation_sets','immutable_snapshot','deny_snapshot_mutation'),
 ('audit_logs','immutable_snapshot','deny_snapshot_mutation'),
 ('building_contacts','set_updated_at','update_updated_at_column'),
 ('building_floors','floor_identity','guard_identity_change'),
 ('building_floors','set_updated_at','update_updated_at_column'),
 ('building_locations','set_updated_at','update_updated_at_column'),
 ('buildings','core_audit','record_core_audit'),
 ('buildings','owned_building','validate_owned_content'),
 ('buildings','set_updated_at','update_updated_at_column'),
 ('debrief_artifacts','immutable_snapshot','deny_snapshot_mutation'),
 ('feedback','set_ts_feedback','update_updated_at_column'),
 ('invoice_metadata','set_ts_invoice_metadata','update_updated_at_column'),
 ('organizations','set_updated_at','update_updated_at_column'),
 ('payment_transactions','enforce_payment_transaction_state_before_write','enforce_payment_transaction_state'),
 ('payos_payment_requests','set_ts_payos_payment_requests','update_updated_at_column'),
 ('payos_payment_requests','validate_payos_paid_before_write','validate_payos_paid_request'),
 ('processing_jobs','processing_job_write','validate_processing_job'),
 ('processing_jobs','retained_history','deny_snapshot_mutation'),
 ('quotations','set_ts_quotations','update_updated_at_column'),
 ('release_packages','immutable_snapshot','deny_snapshot_mutation'),
 ('release_packages','package_insert','validate_package'),
 ('release_qr_codes','core_audit','record_core_audit'),
 ('release_qr_codes','qr_write','validate_release_qr_code'),
 ('release_qr_codes','retained_history','deny_snapshot_mutation'),
 ('releases','core_audit','record_core_audit'),
 ('releases','release_write','validate_release_write'),
 ('releases','retained_history','deny_snapshot_mutation'),
 ('releases','set_updated_at','update_updated_at_column'),
 ('revision_artifacts','artifact_insert','validate_revision_child'),
 ('revision_artifacts','immutable_snapshot','deny_snapshot_mutation'),
 ('revision_floors','floor_insert','validate_revision_child'),
 ('revision_floors','immutable_snapshot','deny_snapshot_mutation'),
 ('revision_issues','immutable_snapshot','deny_snapshot_mutation'),
 ('revision_processing_logs','immutable_snapshot','deny_snapshot_mutation'),
 ('revision_reviews','core_audit','record_core_audit'),
 ('revision_reviews','immutable_snapshot','deny_snapshot_mutation'),
 ('revision_reviews','review_apply','apply_revision_review_action'),
 ('revision_reviews','review_validate','validate_revision_review_action'),
 ('revisions','core_audit','record_core_audit'),
 ('revisions','owned_revision','validate_owned_content'),
 ('revisions','retained_history','deny_snapshot_mutation'),
 ('revisions','revision_state','enforce_revision_status_transition'),
 ('revisions','set_updated_at','update_updated_at_column'),
 ('scenario_versions','core_audit','record_core_audit'),
 ('scenario_versions','immutable_snapshot','deny_snapshot_mutation'),
 ('scenario_versions','scenario_insert','validate_scenario_version_write'),
 ('scenarios','immutable_snapshot','deny_snapshot_mutation'),
 ('scenarios','owned_scenario','validate_owned_content'),
 ('service_packages','set_ts_service_packages','update_updated_at_column'),
 ('session_checkpoints','checkpoint_insert','validate_session_child'),
 ('session_checkpoints','immutable_snapshot','deny_snapshot_mutation'),
 ('session_events','event_insert','validate_session_child'),
 ('session_events','immutable_snapshot','deny_snapshot_mutation'),
 ('session_results','core_audit','record_core_audit'),
 ('session_results','immutable_snapshot','deny_snapshot_mutation'),
 ('session_results','result_insert','validate_session_child'),
 ('session_results','result_terminal','verify_result_committed_terminal'),
 ('sessions','core_audit','record_core_audit'),
 ('sessions','retained_history','deny_snapshot_mutation'),
 ('sessions','session_write','validate_training_session'),
 ('source_documents','core_audit','record_core_audit'),
 ('source_documents','source_no_delete','deny_snapshot_mutation'),
 ('source_documents','source_write','validate_source'),
 ('support_tickets','set_ts_support_tickets','update_updated_at_column'),
 ('trainings','retained_history','deny_snapshot_mutation'),
 ('trainings','set_updated_at','update_updated_at_column'),
 ('trainings','training_write','validate_training_write'),
 ('user_devices','device_identity','guard_identity_change'),
 ('users','set_updated_at','update_updated_at_column'),
 ('users','user_identity','guard_identity_change'),
 ('validation_runs','immutable_snapshot','deny_snapshot_mutation'),
 ('validation_runs','qa_insert','validate_qa_run')
 ) AS legacy(table_name,trigger_name,function_name) LOOP
  rel:=to_regclass(format('public.%I',pair.table_name));
  CONTINUE WHEN rel IS NULL OR NOT EXISTS(SELECT 1 FROM pg_trigger t JOIN pg_proc p ON p.oid=t.tgfoid
   WHERE t.tgrelid=rel AND t.tgname=pair.trigger_name AND p.proname=pair.function_name AND NOT t.tgisinternal);
  SELECT pg_get_userbyid(relowner) INTO owner FROM pg_class WHERE oid=rel;
  -- Dropping a trigger needs the table owner's privileges; borrow them for this transaction when the migration role lacks them.
  IF NOT pg_has_role(current_user,owner,'USAGE') THEN
   EXECUTE format('GRANT %I TO %I WITH INHERIT TRUE',owner,current_user);
   granted:=granted||owner;
  END IF;
  EXECUTE format('DROP TRIGGER %I ON public.%I',pair.trigger_name,pair.table_name);
 END LOOP;
 FOREACH r IN ARRAY granted LOOP EXECUTE format('REVOKE %I FROM %I',r,current_user);END LOOP;
 IF EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='extensions') THEN
  FOR r IN SELECT rolname FROM pg_roles WHERE rolname ~ '^(fire3d|fet3d)_' LOOP
   EXECUTE format('GRANT USAGE ON SCHEMA extensions TO %I',r);
  END LOOP;
 END IF;
END $$;
