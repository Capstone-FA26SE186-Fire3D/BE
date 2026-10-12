-- Expiry reminder delivery lease: a claim records Sending + lease token/expiry before its transaction ends, so two workers never
-- take the same delivery and a crashed worker's delivery is reclaimed after the lease expires. Email stays at-least-once.
-- Additive; existing deliveries keep their status (none is in flight before this migration).
ALTER TABLE notification_deliveries ADD COLUMN IF NOT EXISTS lease_token uuid,ADD COLUMN IF NOT EXISTS lease_until timestamptz,
 ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE notification_deliveries DROP CONSTRAINT IF EXISTS check_notification_delivery_status;
ALTER TABLE notification_deliveries ADD CONSTRAINT check_notification_delivery_status CHECK(status IN('Pending','Sending','Sent','Failed'));
ALTER TABLE notification_deliveries DROP CONSTRAINT IF EXISTS check_notification_delivery_lease;
ALTER TABLE notification_deliveries ADD CONSTRAINT check_notification_delivery_lease CHECK((status='Sending')=(lease_token IS NOT NULL AND lease_until IS NOT NULL));
CREATE INDEX IF NOT EXISTS notification_deliveries_claim ON notification_deliveries(next_attempt_at,created_at) WHERE channel='Email' AND status IN('Pending','Sending');
CREATE INDEX IF NOT EXISTS organization_notifications_recipient ON organization_notifications(recipient_user_id,created_at DESC,id);
