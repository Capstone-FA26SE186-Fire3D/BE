-- BE#140: expiry reminders also go to the recipient's live FCM devices as a third delivery channel, Push.
-- Additive: existing deliveries are Web/Email; the claim index now covers both channels the worker sends.
ALTER TABLE public.notification_deliveries DROP CONSTRAINT IF EXISTS check_notification_channel;
ALTER TABLE public.notification_deliveries ADD CONSTRAINT check_notification_channel CHECK (channel IN ('Web','Email','Push'));
DROP INDEX IF EXISTS public.notification_deliveries_claim;
CREATE INDEX notification_deliveries_claim ON public.notification_deliveries(next_attempt_at,created_at)
 WHERE channel IN('Email','Push') AND status IN('Pending','Sending');
