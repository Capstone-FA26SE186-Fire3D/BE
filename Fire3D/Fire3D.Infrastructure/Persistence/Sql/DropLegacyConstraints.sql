-- Production drift found on 19/10/2026 (BE#118, first pulled Geometry job): besides the hand-made triggers removed by
-- AlignProductionSchema, the Supabase database still carries CHECK constraints from the Docs v7 SQL scripts that no migration
-- creates. Some contradict what the gates write, so the first Geometry Complete failed with 500 (revision_artifacts only allowed
-- Preview/Geometry/..., validation_runs required a Geometry run without candidate artifact) and later steps would fail the same way:
-- package jobs (processing_jobs kind), editor scenario versions (array spawn points, goals, hazards and NPCs, empty mode policy,
-- time limits above 3600 s), learner completion (sessions without content_status_at_completion, results without exit id), QA
-- severities other than Info/Warning/Error and non-Android build targets. Production has no artifact, scenario version, release or
-- session yet, so no data depends on them.
-- The constraints were found by applying every production-only constraint to the test database and running the suite: each one
-- below is violated by the gates themselves. Constraints violated only by test fixtures stay (they match what the code writes).
-- A constraint is dropped only when its name and exact definition match the legacy one, so a migrated database is untouched.
-- Billing/Schema.sql adds check_quotation_amounts and check_quotation_issued only when the name is free; production kept the older
-- definitions under those names (a cancelled draft quotation was refused), so they are replaced by the current ones.
DO $$
DECLARE item record;
BEGIN
 FOR item IN SELECT * FROM (VALUES
  ('processing_jobs','processing_jobs_check','CHECK ((((kind = ''Geometry''::text) AND (scenario_version_id IS NULL)) OR ((kind = ''Scenario''::text) AND (scenario_version_id IS NOT NULL))))'),
  ('processing_jobs','processing_jobs_kind_check','CHECK ((kind = ANY (ARRAY[''Geometry''::text, ''Scenario''::text])))'),
  ('quotations','check_quotation_amounts','CHECK (((unit_price >= (0)::numeric) AND (subtotal_amount = (unit_price * (quantity)::numeric)) AND (tax_amount >= (0)::numeric) AND (discount_amount >= (0)::numeric) AND (total_amount = ((subtotal_amount + tax_amount) - discount_amount)) AND (total_amount >= (0)::numeric)))'),
  ('quotations','check_quotation_issued','CHECK (((status = ''Draft''::quotation_status_enum) OR ((issued_by IS NOT NULL) AND (issued_at IS NOT NULL))))'),
  ('release_packages','release_packages_build_target_check','CHECK ((build_target = ''Android''::text))'),
  ('revision_artifacts','revision_artifacts_artifact_type_check','CHECK ((artifact_type = ANY (ARRAY[''Preview''::text, ''Geometry''::text, ''Graph''::text, ''HazardGrid''::text, ''CandidatePackage''::text])))'),
  ('revision_issues','revision_issues_severity_check','CHECK ((severity = ANY (ARRAY[''Info''::text, ''Warning''::text, ''Error''::text])))'),
  ('scenario_versions','scenario_versions_fire_source_config_check','CHECK ((jsonb_typeof(fire_source_config) = ''object''::text))'),
  ('scenario_versions','scenario_versions_goal_config_check','CHECK (((jsonb_typeof(goal_config) = ''object''::text) AND (goal_config ? ''exitIds''::text)))'),
  ('scenario_versions','scenario_versions_mode_policy_check','CHECK (((jsonb_typeof(mode_policy) = ''object''::text) AND (mode_policy ?& ARRAY[''Learn''::text, ''Guided''::text, ''Assessment''::text])))'),
  ('scenario_versions','scenario_versions_npc_config_check','CHECK ((jsonb_typeof(npc_config) = ''object''::text))'),
  ('scenario_versions','scenario_versions_replan_interval_seconds_check','CHECK ((replan_interval_seconds > 0))'),
  ('scenario_versions','scenario_versions_spawn_config_check','CHECK (((jsonb_typeof(spawn_config) = ''object''::text) AND (spawn_config ? ''nodeId''::text)))'),
  ('scenario_versions','scenario_versions_time_limit_seconds_check','CHECK (((time_limit_seconds >= 1) AND (time_limit_seconds <= 3600)))'),
  ('session_results','session_results_check','CHECK (((NOT reached_exit) OR (NULLIF(btrim(exit_point_id), ''''::text) IS NOT NULL)))'),
  ('sessions','sessions_check2','CHECK ((((status = ANY (ARRAY[''Created''::session_status_enum, ''Launching''::session_status_enum, ''Running''::session_status_enum])) AND (ended_at IS NULL) AND (content_status_at_completion IS NULL)) OR ((status <> ALL (ARRAY[''Created''::session_status_enum, ''Launching''::session_status_enum, ''Running''::session_status_enum])) AND (ended_at IS NOT NULL) AND (content_status_at_completion IS NOT NULL))))'),
  ('validation_runs','validation_runs_check','CHECK ((((kind = ''Geometry''::text) AND (scenario_version_id IS NULL) AND (scenario_hash IS NULL) AND (candidate_artifact_id IS NULL)) OR ((kind = ''Scenario''::text) AND (scenario_version_id IS NOT NULL) AND (candidate_artifact_id IS NOT NULL) AND (scenario_hash IS NOT NULL) AND ((scenario_hash)::text ~ ''^[0-9a-f]{64}$''::text))))'),
  ('validation_runs','validation_runs_kind_check','CHECK ((kind = ANY (ARRAY[''Geometry''::text, ''Scenario''::text])))')
 ) v(tbl,name,def) LOOP
  IF EXISTS(SELECT 1 FROM pg_constraint c WHERE c.conrelid=to_regclass('public.'||item.tbl) AND c.conname=item.name AND pg_get_constraintdef(c.oid)=item.def) THEN
   EXECUTE format('ALTER TABLE public.%I DROP CONSTRAINT %I',item.tbl,item.name);
  END IF;
 END LOOP;
END $$;
DO $$ BEGIN IF to_regclass('public.quotations') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_amounts' AND conrelid='public.quotations'::regclass) THEN ALTER TABLE public.quotations ADD CONSTRAINT check_quotation_amounts CHECK (
        unit_price >= 0
        AND subtotal_amount >= 0
        AND tax_amount >= 0
        AND discount_amount >= 0
        AND total_amount = subtotal_amount + tax_amount - discount_amount
        AND total_amount >= 0
    ); END IF; END $$;
DO $$ BEGIN IF to_regclass('public.quotations') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_issued' AND conrelid='public.quotations'::regclass) THEN ALTER TABLE public.quotations ADD CONSTRAINT check_quotation_issued CHECK (
        status IN ('Draft','Cancelled') OR (issued_by IS NOT NULL AND issued_at IS NOT NULL)
    ); END IF; END $$;
