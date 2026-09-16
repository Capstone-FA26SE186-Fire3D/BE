-- Read-only post-migration verification. Run as migration owner, ON_ERROR_STOP=1.
DO $verify$
DECLARE n text; r text; f record;
BEGIN
 FOREACH n IN ARRAY ARRAY['organizations','users','user_devices','buildings','building_locations','building_floors','building_contacts','revisions','source_documents','annotation_sets','processing_jobs','revision_processing_logs','revision_floors','scenarios','scenario_versions','revision_artifacts','validation_runs','revision_issues','revision_reviews','releases','release_packages','trainings','release_qr_codes','sessions','session_events','session_results','session_checkpoints','debrief_artifacts','audit_logs','service_packages','quotations','payos_payment_requests','payment_transactions','invoice_metadata','feedback','support_tickets','auth_refresh_tokens'] LOOP
  IF to_regclass('public.'||n) IS NULL THEN RAISE EXCEPTION 'Missing table %',n; END IF;
  IF NOT (SELECT relrowsecurity FROM pg_class WHERE oid=to_regclass('public.'||n)) THEN
   RAISE EXCEPTION 'RLS disabled on %',n;
  END IF;
  FOREACH r IN ARRAY ARRAY['anon','authenticated','service_role'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) AND
      has_table_privilege(r, 'public.'||n, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER') THEN
    RAISE EXCEPTION 'Unexpected client privilege: % / %',r,n;
   END IF;
  END LOOP;
 END LOOP;
 FOREACH n IN ARRAY ARRAY['append_session_event','apply_revision_review_action','apply_verified_payos_webhook','assert_org_user','complete_training_session','confirm_revision_for_training','create_pending_payos_payment_request','deny_snapshot_mutation','enforce_payment_transaction_state','enforce_revision_status_transition','guard_identity_change','lock_content','record_core_audit','start_training_session','update_updated_at_column','validate_owned_content','validate_package','validate_payos_paid_request','validate_processing_job','validate_qa_run','validate_release_qr_code','validate_release_write','validate_revision_child','validate_revision_review_action','validate_scenario_version_write','validate_session_child','validate_source','validate_training_session','validate_training_write','verify_result_committed_terminal'] LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace ns ON ns.oid=p.pronamespace WHERE ns.nspname='public' AND p.proname=n) THEN
   RAISE EXCEPTION 'Missing function %',n;
  END IF;
 END LOOP;
 FOR f IN SELECT p.oid FROM pg_proc p JOIN pg_namespace ns ON ns.oid=p.pronamespace
  WHERE ns.nspname='public' AND p.proname=ANY(ARRAY['append_session_event','apply_revision_review_action','apply_verified_payos_webhook','assert_org_user','complete_training_session','confirm_revision_for_training','create_pending_payos_payment_request','deny_snapshot_mutation','enforce_payment_transaction_state','enforce_revision_status_transition','guard_identity_change','lock_content','record_core_audit','start_training_session','update_updated_at_column','validate_owned_content','validate_package','validate_payos_paid_request','validate_processing_job','validate_qa_run','validate_release_qr_code','validate_release_write','validate_revision_child','validate_revision_review_action','validate_scenario_version_write','validate_session_child','validate_source','validate_training_session','validate_training_write','verify_result_committed_terminal']) LOOP
  FOREACH r IN ARRAY ARRAY['anon','authenticated','service_role'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) AND has_function_privilege(r,f.oid,'EXECUTE') THEN
    RAISE EXCEPTION 'Unexpected client function EXECUTE: % / %',r,f.oid::regprocedure;
   END IF;
  END LOOP;
 END LOOP;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api' AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolcanlogin)) THEN
  RAISE EXCEPTION 'fire3d_api role attributes are too broad';
 END IF;
 IF NOT has_table_privilege('fire3d_api','public.users','INSERT') OR
    NOT has_table_privilege('fire3d_api','public.auth_refresh_tokens','UPDATE') OR
    has_table_privilege('fire3d_api','public.audit_logs','UPDATE') OR
    has_table_privilege('fire3d_api','public.payment_transactions','INSERT') OR
    pg_has_role('fire3d_api','fet3d_payos_ledger_owner','MEMBER') THEN
  RAISE EXCEPTION 'Runtime role grants do not match auth/admin boundary';
 END IF;
 IF (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='public.payment_transactions'::regclass)<>'fet3d_payos_ledger_owner' THEN
  RAISE EXCEPTION 'Payment ledger owner is incorrect';
 END IF;
END $verify$;
SELECT 'PASS: Fire3D tables/functions/RLS/client denial/runtime grants verified' AS result;
