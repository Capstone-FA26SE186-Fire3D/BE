-- Forward-only expansion. No legacy backfill or commercial defaults.
CREATE TABLE IF NOT EXISTS billing_quota_policy_versions (
 id uuid PRIMARY KEY, audience text NOT NULL, policy_kind text NOT NULL,
 quota_unit text NOT NULL, effective_from timestamptz NOT NULL, effective_until timestamptz,
 rollover text NOT NULL, created_by uuid NOT NULL REFERENCES users(id), created_at timestamptz NOT NULL);
ALTER TABLE billing_quota_policy_versions ADD CONSTRAINT billing_quota_policy_contract
 CHECK(audience='organization' AND policy_kind='quota' AND rollover='None'
 AND quota_unit ~ '^[a-z][a-z0-9_-]{0,49}$' AND (effective_until IS NULL OR effective_until>effective_from));
CREATE FUNCTION immutable_billing_quota_policy() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'Billing quota policy versions are immutable' USING ERRCODE='23514'; END $$;
CREATE TRIGGER billing_quota_policy_immutable BEFORE UPDATE OR DELETE ON billing_quota_policy_versions
 FOR EACH ROW EXECUTE FUNCTION immutable_billing_quota_policy();
ALTER TABLE service_packages ADD COLUMN IF NOT EXISTS commercial_version integer NOT NULL DEFAULT 1,
 ADD COLUMN IF NOT EXISTS learner_limit integer, ADD COLUMN IF NOT EXISTS ai_quota_units integer,
 ADD COLUMN IF NOT EXISTS ai_policy_version_id uuid;
ALTER TABLE service_packages ADD CONSTRAINT service_package_quota_policy_fk FOREIGN KEY(ai_policy_version_id) REFERENCES billing_quota_policy_versions(id);
ALTER TABLE service_packages ALTER COLUMN commercial_version SET DEFAULT 1;
ALTER TABLE service_packages ADD CONSTRAINT service_package_v7_contract CHECK
 (commercial_version IN(1,7) AND (commercial_version<>7 OR (duration_months IN(6,12) AND learner_limit IS NOT NULL
 AND learner_limit>0 AND ai_quota_units IS NOT NULL AND ai_quota_units>=0 AND (ai_quota_units=0 OR ai_policy_version_id IS NOT NULL))));
GRANT SELECT,INSERT ON billing_quota_policy_versions TO fire3d_api;
REVOKE UPDATE,DELETE ON billing_quota_policy_versions FROM fire3d_api;
REVOKE ALL ON billing_quota_policy_versions FROM PUBLIC;
DO $roles$ DECLARE r text; BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   EXECUTE format('REVOKE ALL ON public.billing_quota_policy_versions FROM %I',r);
  END IF;
 END LOOP;
END $roles$;
ALTER TABLE billing_quota_policy_versions ENABLE ROW LEVEL SECURITY;
CREATE POLICY billing_quota_policy_api ON billing_quota_policy_versions TO fire3d_api
 USING(true) WITH CHECK(audience='organization' AND policy_kind='quota');
