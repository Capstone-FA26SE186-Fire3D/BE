-- Issue #94 / FR-BUILD-01: POST /api/buildings needs Idempotency-Key. The v7 schema already defines application_command_receipts
-- for API commands that have no receipt table of their own; it is created here exactly as v7 defines it. The API login only
-- reads and inserts receipts (operation 'Building.Create'); fet3d_backend_executor keeps the v7 grant. Additive and idempotent.
CREATE TABLE IF NOT EXISTS application_command_receipts (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_user_id       UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL,
    organization_id     UUID REFERENCES organizations(id) ON DELETE RESTRICT,
    operation_name      VARCHAR(100) NOT NULL,
    idempotency_key     VARCHAR(255) NOT NULL,
    input_hash          VARCHAR(128) NOT NULL,
    result_status       VARCHAR(30) NOT NULL,
    result_payload      JSONB NOT NULL DEFAULT '{}',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (actor_user_id, operation_name, idempotency_key),
    CONSTRAINT check_command_receipt_key CHECK (NULLIF(pg_catalog.btrim(idempotency_key), '') IS NOT NULL),
    CONSTRAINT check_command_receipt_hash CHECK (input_hash ~ '^[0-9a-fA-F]{64,128}$'),
    CONSTRAINT check_command_receipt_status CHECK (result_status IN ('Succeeded','Rejected'))
);
CREATE INDEX IF NOT EXISTS idx_command_receipts_operation ON application_command_receipts(operation_name, created_at DESC);
ALTER TABLE application_command_receipts ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON application_command_receipts FROM PUBLIC;
DO $$
DECLARE r text;
BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON application_command_receipts FROM %I',r);END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_backend_executor') THEN
  GRANT SELECT,INSERT,UPDATE,DELETE ON application_command_receipts TO fet3d_backend_executor;
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='application_command_receipts' AND policyname='command_receipts_executor') THEN
   CREATE POLICY command_receipts_executor ON application_command_receipts TO fet3d_backend_executor USING(true) WITH CHECK(true);
  END IF;
 END IF;
 -- Pending-registration cleanup deletes users; the RESTRICT foreign key check must be able to see receipts.
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  GRANT SELECT ON application_command_receipts TO fet3d_pending_cleanup_owner;
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='application_command_receipts' AND policyname='pending_cleanup_owner') THEN
   CREATE POLICY pending_cleanup_owner ON application_command_receipts FOR SELECT TO fet3d_pending_cleanup_owner USING(true);
  END IF;
 END IF;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
  GRANT SELECT,INSERT ON application_command_receipts TO fire3d_api;
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='application_command_receipts' AND policyname='fire3d_api_runtime') THEN
   CREATE POLICY fire3d_api_runtime ON application_command_receipts TO fire3d_api USING(true) WITH CHECK(true);
  END IF;
 END IF;
END $$;
