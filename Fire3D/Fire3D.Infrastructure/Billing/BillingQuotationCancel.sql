-- Quotation cancellation: a payment that arrives after the quotation was cancelled stays Paid/Applied with its provenance,
-- while its provisioning records stay in NeedsReconcile (PAYOS_QUOTATION_CANCELLED). No service or quota is activated and
-- the quotation is never reopened. Additive; existing payments and records keep their values.
CREATE TEMP TABLE billing_cancel_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_payos_ledger_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_payos_ledger_owner','SET') OR NOT pg_has_role(current_user,'fet3d_payos_ledger_owner','USAGE');
 INSERT INTO billing_cancel_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_payos_ledger_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
END $$;

CREATE OR REPLACE FUNCTION claim_payos_provisioning_context(p_line uuid,p_lease uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE rec public.payment_provisioning_records%ROWTYPE; item public.quotation_building_items%ROWTYPE;
 req public.payos_payment_requests%ROWTYPE; b public.buildings%ROWTYPE; last_end timestamptz; eid uuid; q public.quotations%ROWTYPE;
 t public.quotation_topup_items%ROWTYPE; e public.service_entitlements%ROWTYPE;
BEGIN
 IF p_lease IS NULL THEN RAISE EXCEPTION 'PAYOS_STALE_LEASE'; END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO rec FROM public.payment_provisioning_records WHERE id=p_line;
 IF NOT FOUND THEN RAISE EXCEPTION 'PAYOS_PROVISIONING_NOT_FOUND'; END IF;
 PERFORM 1 FROM public.payment_transactions WHERE id=rec.payment_transaction_id AND status='Applied' FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'PAYOS_PAYMENT_NOT_APPLIED'; END IF;
 PERFORM locked_building.id FROM public.buildings locked_building JOIN public.quotation_building_items i ON i.building_id=locked_building.id WHERE i.quotation_id=rec.quotation_id ORDER BY locked_building.id FOR UPDATE OF locked_building;
 SELECT * INTO rec FROM public.payment_provisioning_records WHERE id=p_line FOR UPDATE;
 IF rec.lease_until IS NULL OR rec.lease_token IS DISTINCT FROM p_lease OR rec.lease_until<=clock_timestamp() THEN RAISE EXCEPTION 'PAYOS_STALE_LEASE'; END IF;
 SELECT * INTO q FROM public.quotations WHERE id=rec.quotation_id;
 -- Money received for a cancelled quotation is kept, but nothing is provisioned: the record waits for operator reconciliation.
 IF q.status='Cancelled' THEN RAISE EXCEPTION 'PAYOS_QUOTATION_CANCELLED'; END IF;
 SELECT * INTO req FROM public.payos_payment_requests WHERE paid_transaction_id=rec.payment_transaction_id;
 IF req.status IS DISTINCT FROM 'Paid' OR req.paid_at IS NULL THEN RAISE EXCEPTION 'PAYOS_PAYMENT_NOT_APPLIED'; END IF;
 IF q.billing_purpose='AIQuotaTopUp' THEN
  SELECT * INTO t FROM public.quotation_topup_items WHERE id=rec.quotation_item_id AND quotation_id=q.id;
  SELECT id INTO eid FROM public.billing_ai_quota_grants WHERE provisioning_key='quota:'||rec.provisioning_key;
  IF eid IS NOT NULL THEN RETURN jsonb_build_object('Activation',t.starts_at,'LastEnd',NULL,'Months',0,'ExistingId',eid); END IF;
  IF t.id IS NULL OR NOT EXISTS(SELECT 1 FROM public.organizations WHERE id=rec.organization_id AND is_active AND deleted_at IS NULL) THEN RAISE EXCEPTION 'PAYOS_TOPUP_UNAVAILABLE'; END IF;
  IF t.ends_at<=clock_timestamp() OR req.paid_at>q.valid_until THEN RAISE EXCEPTION 'PAYOS_TOPUP_NEEDS_RECONCILE'; END IF;
  RETURN jsonb_build_object('Activation',t.starts_at,'LastEnd',NULL,'Months',0,'ExistingId',NULL);
 END IF;
 SELECT * INTO item FROM public.quotation_building_items WHERE id=rec.quotation_item_id;
 IF item.purchase_action='Upgrade' THEN
  SELECT id INTO eid FROM public.billing_entitlement_upgrades WHERE provisioning_key=rec.provisioning_key;
  IF eid IS NOT NULL THEN RETURN jsonb_build_object('Activation',item.starts_at,'LastEnd',NULL,'Months',0,'ExistingId',eid); END IF;
  SELECT * INTO e FROM public.service_entitlements WHERE id=item.upgrade_entitlement_id FOR UPDATE;
  SELECT * INTO b FROM public.buildings WHERE id=item.building_id;
  IF b.organization_id IS DISTINCT FROM rec.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL
   OR NOT EXISTS(SELECT 1 FROM public.organizations WHERE id=rec.organization_id AND is_active AND deleted_at IS NULL) THEN RAISE EXCEPTION 'PAYOS_BUILDING_UNAVAILABLE'; END IF;
  -- Late payment after the baseline changed stays Paid/Applied and needs reconcile; nothing is applied to a moved baseline.
  IF e.id IS NULL OR e.status<>'Active' OR e.ends_at<=clock_timestamp() OR req.paid_at>q.valid_until OR e.ends_at IS DISTINCT FROM item.ends_at
   OR public.billing_capacity_revision(e.id)<>item.upgrade_base_capacity_revision
   OR NOT EXISTS(SELECT 1 FROM public.billing_upgrade_reservations r JOIN public.billing_checkout_operations op ON op.id=r.checkout_id
    WHERE op.payment_request_id=req.id AND r.quotation_item_id=item.id AND r.status='Reserved' AND r.base_capacity_revision=item.upgrade_base_capacity_revision)
  THEN RAISE EXCEPTION 'PAYOS_UPGRADE_NEEDS_RECONCILE'; END IF;
  RETURN jsonb_build_object('Activation',item.starts_at,'LastEnd',NULL,'Months',0,'ExistingId',NULL);
 END IF;
 SELECT id INTO eid FROM public.service_entitlements WHERE provisioning_key=rec.provisioning_key;
 -- Existing provenance wins over expiry/lifecycle checks; recovery never grants twice.
 IF eid IS NOT NULL THEN
  RETURN jsonb_build_object('Activation',coalesce(item.starts_at,req.paid_at),'LastEnd',NULL,'Months',item.service_duration_months,'ExistingId',eid);
 END IF;
 SELECT * INTO b FROM public.buildings WHERE id=item.building_id;
 IF b.organization_id IS DISTINCT FROM rec.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL
  OR NOT EXISTS(SELECT 1 FROM public.organizations WHERE id=rec.organization_id AND is_active AND deleted_at IS NULL)
  OR q.billing_purpose<>'BuildingService' THEN RAISE EXCEPTION 'PAYOS_BUILDING_UNAVAILABLE'; END IF;
 IF item.commercial_version=7 THEN
  IF item.ends_at<=clock_timestamp() OR req.paid_at>item.starts_at OR req.paid_at>q.valid_until
   OR NOT EXISTS(SELECT 1 FROM public.billing_service_reservations r JOIN public.billing_checkout_operations op ON op.id=r.checkout_id
    WHERE op.payment_request_id=req.id AND r.quotation_item_id=item.id AND r.status='Reserved' AND r.starts_at=item.starts_at AND r.ends_at=item.ends_at)
   OR EXISTS(SELECT 1 FROM public.service_entitlements e2 WHERE e2.building_id=b.id AND e2.payment_transaction_id IS NOT NULL AND e2.status IN('Active','Expired','Suspended')
    AND tstzrange(e2.starts_at,e2.ends_at,'[)') && tstzrange(item.starts_at,item.ends_at,'[)'))
  THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_NEEDS_RECONCILE'; END IF;
  RETURN jsonb_build_object('Activation',item.starts_at,'LastEnd',NULL,'Months',item.service_duration_months,'ExistingId',NULL);
 END IF;
 SELECT max(ends_at) INTO last_end FROM public.service_entitlements WHERE building_id=b.id AND payment_transaction_id IS NOT NULL AND status IN('Active','Expired','Suspended');
 IF item.purchase_action='New' AND last_end>req.paid_at THEN RAISE EXCEPTION 'PAYOS_NEW_ALREADY_ENTITLED'; END IF;
 RETURN jsonb_build_object('Activation',req.paid_at,'LastEnd',CASE WHEN item.purchase_action='Renewal' THEN last_end ELSE NULL END,'Months',item.service_duration_months,'ExistingId',NULL);
END $$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM billing_cancel_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_payos_ledger_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_payos_ledger_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
