-- The deployed runtime login is a member of fire3d_api. Avatar uploads and the PayOS/billing paths read and write these tables
-- directly, but fire3d_api never received table grants for them: avatar upload answered 500 and PayOS recovery failed with
-- permission denied. These grants mirror the reviewed general-API identity in payos-deployment.md: money tables are read-only,
-- provisioning records only move lease/status, and operations/receipts/inbox/quotation data are writable. RLS policies are
-- added where the table enforces RLS and had no fire3d_api policy. No grant to anon/authenticated; no data changes.
DO $$
DECLARE t text;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN RETURN;END IF;
 GRANT SELECT,INSERT,UPDATE ON TABLE public.avatar_upload_intents TO fire3d_api;
 GRANT SELECT,INSERT,UPDATE,DELETE ON TABLE public.billing_checkout_operations,public.billing_command_receipts,public.building_contacts,
  public.building_locations,public.enterprise_quote_requests,public.payos_webhook_inbox,public.quotation_building_items,public.quotations,
  public.service_package_discount_rules,public.service_packages TO fire3d_api;
 GRANT SELECT ON TABLE public.payment_provisioning_records,public.payment_transactions,public.payos_payment_requests,public.service_entitlements TO fire3d_api;
 GRANT UPDATE(attempts,last_error,lease_token,lease_until,next_attempt_at,status,updated_at) ON TABLE public.payment_provisioning_records TO fire3d_api;
 FOREACH t IN ARRAY ARRAY['building_contacts','building_locations','quotations','service_packages'] LOOP
  EXECUTE format('DROP POLICY IF EXISTS fire3d_api_billing_app ON public.%I',t);
  EXECUTE format('CREATE POLICY fire3d_api_billing_app ON public.%I TO fire3d_api USING (true) WITH CHECK (true)',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['payment_transactions','payos_payment_requests'] LOOP
  EXECUTE format('DROP POLICY IF EXISTS fire3d_api_billing_read ON public.%I',t);
  EXECUTE format('CREATE POLICY fire3d_api_billing_read ON public.%I FOR SELECT TO fire3d_api USING (true)',t);
 END LOOP;
END $$;
