-- Additive runtime storage; no legacy billing data is removed.
ALTER TABLE billing_checkout_operations
 ADD COLUMN IF NOT EXISTS provider_input jsonb NOT NULL DEFAULT '{}',
 ADD COLUMN IF NOT EXISTS payment_link_id varchar(100),
 ADD COLUMN IF NOT EXISTS payment_request_id uuid REFERENCES payos_payment_requests(id),
 ADD COLUMN IF NOT EXISTS expires_at timestamptz,
 ADD COLUMN IF NOT EXISTS attempts int NOT NULL DEFAULT 0,
 ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NOT NULL DEFAULT now(),
 ADD COLUMN IF NOT EXISTS last_error varchar(80),
 ADD COLUMN IF NOT EXISTS cancel_requested boolean NOT NULL DEFAULT false;
ALTER TABLE billing_checkout_operations DROP CONSTRAINT IF EXISTS billing_checkout_operations_status_check;
ALTER TABLE billing_checkout_operations DROP CONSTRAINT IF EXISTS check_payos_checkout_status;
ALTER TABLE billing_checkout_operations ADD CONSTRAINT check_payos_checkout_status
 CHECK(status IN('Creating','Ready','NeedsReconcile','Failed','Expired','Cancelled','Completed'));
ALTER TABLE payos_payment_requests ADD COLUMN IF NOT EXISTS payment_link_id varchar(100);
CREATE UNIQUE INDEX IF NOT EXISTS ux_payos_payment_link_id ON payos_payment_requests(payment_link_id);
ALTER TABLE payos_webhook_inbox ADD COLUMN IF NOT EXISTS last_error varchar(80);
ALTER TABLE payos_webhook_inbox ADD COLUMN IF NOT EXISTS processed_at timestamptz;
ALTER TABLE payment_provisioning_records
 ADD COLUMN IF NOT EXISTS lease_token uuid,
 ADD COLUMN IF NOT EXISTS lease_until timestamptz,
 ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NOT NULL DEFAULT now();
CREATE INDEX IF NOT EXISTS idx_payos_checkout_retry ON billing_checkout_operations(status,next_attempt_at);
CREATE INDEX IF NOT EXISTS idx_payos_provisioning_retry ON payment_provisioning_records(status,next_attempt_at);
CREATE SEQUENCE IF NOT EXISTS fet3d_payos_order_code MINVALUE 1 MAXVALUE 9007199254740991 START 10000000;
SELECT setval('fet3d_payos_order_code',greatest(
 (SELECT last_value FROM fet3d_payos_order_code),
 coalesce((SELECT max(order_code) FROM billing_checkout_operations),0),
 coalesce((SELECT max(order_code) FROM payos_payment_requests),0)),true);
REVOKE ALL ON SEQUENCE fet3d_payos_order_code FROM PUBLIC;
