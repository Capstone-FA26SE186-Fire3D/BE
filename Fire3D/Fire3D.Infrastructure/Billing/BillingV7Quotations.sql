ALTER TABLE quotations ADD COLUMN IF NOT EXISTS commercial_version integer NOT NULL DEFAULT 1;
ALTER TABLE quotation_building_items
 ADD COLUMN IF NOT EXISTS commercial_version integer NOT NULL DEFAULT 1,
 ADD COLUMN IF NOT EXISTS package_revision bigint,
 ADD COLUMN IF NOT EXISTS learner_limit integer,
 ADD COLUMN IF NOT EXISTS ai_quota_units integer,
 ADD COLUMN IF NOT EXISTS ai_policy_version_id uuid REFERENCES billing_quota_policy_versions(id),
 ADD COLUMN IF NOT EXISTS ai_quota_unit text,
 ADD COLUMN IF NOT EXISTS starts_at timestamptz,
 ADD COLUMN IF NOT EXISTS ends_at timestamptz;
ALTER TABLE quotations ALTER COLUMN commercial_version SET DEFAULT 1;
ALTER TABLE quotation_building_items ALTER COLUMN commercial_version SET DEFAULT 1;
ALTER TABLE quotation_building_items ADD CONSTRAINT quotation_line_v7_configuration CHECK(
 commercial_version IN(1,7) AND (commercial_version<>7 OR
 (service_duration_months IN(6,12) AND package_revision>0 AND package_revision IS NOT NULL
 AND learner_limit>0 AND learner_limit IS NOT NULL AND ai_quota_units>=0 AND ai_quota_units IS NOT NULL
 AND (ai_quota_units=0 OR (ai_policy_version_id IS NOT NULL AND ai_quota_unit IS NOT NULL))
 AND ((starts_at IS NULL AND ends_at IS NULL) OR (starts_at IS NOT NULL AND ends_at IS NOT NULL AND ends_at>starts_at)))));
CREATE FUNCTION validate_v7_quotation_contract() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='UPDATE' AND OLD.status<>'Draft' AND OLD.commercial_version IS DISTINCT FROM NEW.commercial_version THEN
  RAISE EXCEPTION 'Quotation commercial version is immutable' USING ERRCODE='23514';
 END IF;
 IF NEW.commercial_version=7 AND NEW.status IN('Issued','Accepted') AND EXISTS(
  SELECT 1 FROM public.quotation_building_items i LEFT JOIN public.billing_quota_policy_versions p ON p.id=i.ai_policy_version_id
  WHERE i.quotation_id=NEW.id AND (i.commercial_version<>7 OR i.starts_at IS NULL OR i.ends_at IS NULL
   OR i.ends_at IS DISTINCT FROM ((i.starts_at AT TIME ZONE 'UTC')+make_interval(months=>i.service_duration_months)) AT TIME ZONE 'UTC'
   OR NEW.valid_until>i.starts_at OR (i.ai_quota_units>0 AND
    (p.id IS NULL OR p.audience<>'organization' OR p.policy_kind<>'quota' OR p.quota_unit<>i.ai_quota_unit OR p.effective_from>i.starts_at OR p.effective_until<i.ends_at)))
 ) THEN RAISE EXCEPTION 'Incomplete v7 quotation snapshot or service interval' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER billing_v7_quotation BEFORE INSERT OR UPDATE ON quotations FOR EACH ROW EXECUTE FUNCTION validate_v7_quotation_contract();
GRANT SELECT ON billing_quota_policy_versions TO fet3d_payos_ledger_owner;
