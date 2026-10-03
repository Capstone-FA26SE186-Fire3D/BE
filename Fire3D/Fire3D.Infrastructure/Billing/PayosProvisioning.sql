CREATE OR REPLACE FUNCTION claim_payos_provisioning_context(p_line uuid,p_lease uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE rec public.payment_provisioning_records%ROWTYPE; item public.quotation_building_items%ROWTYPE;
 req public.payos_payment_requests%ROWTYPE; b public.buildings%ROWTYPE; last_end timestamptz; eid uuid;
BEGIN
 IF p_lease IS NULL THEN RAISE EXCEPTION 'PAYOS_STALE_LEASE'; END IF;
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 SELECT * INTO rec FROM public.payment_provisioning_records WHERE id=p_line;
 IF NOT FOUND THEN RAISE EXCEPTION 'PAYOS_PROVISIONING_NOT_FOUND'; END IF;
 -- Global lock order: payment -> all payment Buildings ascending -> line.
 PERFORM 1 FROM public.payment_transactions WHERE id=rec.payment_transaction_id AND status='Applied' FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'PAYOS_PAYMENT_NOT_APPLIED'; END IF;
 PERFORM locked_building.id FROM public.buildings locked_building JOIN public.quotation_building_items qi ON qi.building_id=locked_building.id
  WHERE qi.quotation_id=rec.quotation_id ORDER BY locked_building.id FOR UPDATE OF locked_building;
 SELECT * INTO rec FROM public.payment_provisioning_records WHERE id=p_line FOR UPDATE;
 IF p_lease IS NULL OR rec.lease_until IS NULL OR rec.lease_token IS DISTINCT FROM p_lease OR rec.lease_until<=clock_timestamp() OR rec.status='Succeeded' THEN
  RAISE EXCEPTION 'PAYOS_STALE_LEASE'; END IF;
 SELECT * INTO item FROM public.quotation_building_items WHERE id=rec.quotation_item_id;
 SELECT * INTO req FROM public.payos_payment_requests WHERE paid_transaction_id=rec.payment_transaction_id;
 SELECT * INTO b FROM public.buildings WHERE id=item.building_id;
 IF b.organization_id IS DISTINCT FROM rec.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL
  OR NOT EXISTS(SELECT 1 FROM public.organizations WHERE id=rec.organization_id AND is_active AND deleted_at IS NULL)
  OR NOT EXISTS(SELECT 1 FROM public.quotations WHERE id=rec.quotation_id AND billing_purpose='BuildingService') THEN
  RAISE EXCEPTION 'PAYOS_BUILDING_UNAVAILABLE'; END IF;
 IF req.paid_at IS NULL OR req.status<>'Paid' THEN RAISE EXCEPTION 'PAYOS_PAYMENT_NOT_APPLIED'; END IF;
 SELECT id INTO eid FROM public.service_entitlements WHERE provisioning_key=rec.provisioning_key;
 SELECT max(ends_at) INTO last_end FROM public.service_entitlements
  WHERE building_id=b.id AND payment_transaction_id IS NOT NULL AND status IN('Active','Expired','Suspended');
 IF item.purchase_action='New' AND eid IS NULL AND last_end>req.paid_at THEN RAISE EXCEPTION 'PAYOS_NEW_ALREADY_ENTITLED'; END IF;
 UPDATE public.payment_provisioning_records SET lease_until=clock_timestamp()+interval '60 seconds' WHERE id=p_line;
 RETURN jsonb_build_object('Activation',req.paid_at,'LastEnd',CASE WHEN item.purchase_action='Renewal' THEN last_end ELSE NULL END,
  'Months',item.service_duration_months,'ExistingId',eid);
END $$;

CREATE OR REPLACE FUNCTION finalize_payos_provisioning(p_line uuid,p_lease uuid,p_start timestamptz,p_end timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context jsonb; rec public.payment_provisioning_records%ROWTYPE; item public.quotation_building_items%ROWTYPE;
 req public.payos_payment_requests%ROWTYPE; eid uuid; expected timestamptz;
BEGIN
 context:=public.claim_payos_provisioning_context(p_line,p_lease);
 SELECT * INTO rec FROM public.payment_provisioning_records WHERE id=p_line;
 SELECT * INTO item FROM public.quotation_building_items WHERE id=rec.quotation_item_id;
 SELECT * INTO req FROM public.payos_payment_requests WHERE paid_transaction_id=rec.payment_transaction_id;
 eid:=(context->>'ExistingId')::uuid;
 IF eid IS NULL THEN
  expected:=greatest((context->>'Activation')::timestamptz,(context->>'LastEnd')::timestamptz);
  IF p_start IS DISTINCT FROM expected OR p_end IS DISTINCT FROM ((expected AT TIME ZONE 'UTC')+make_interval(months=>item.service_duration_months)) AT TIME ZONE 'UTC' THEN
   RAISE EXCEPTION 'PAYOS_PERIOD_MISMATCH'; END IF;
  INSERT INTO public.service_entitlements(id,organization_id,building_id,service_package_id,quotation_id,quotation_item_id,payment_transaction_id,
   provisioning_key,status,starts_at,ends_at,price_snapshot,terms_snapshot,playtest_units_granted,playtest_units_used,created_by,created_at)
  VALUES(gen_random_uuid(),rec.organization_id,item.building_id,item.service_package_id,rec.quotation_id,item.id,rec.payment_transaction_id,
   rec.provisioning_key,'Active',p_start,p_end,item.price_snapshot,item.terms_snapshot,0,0,req.requested_by,clock_timestamp()) RETURNING id INTO eid;
  INSERT INTO public.audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),rec.organization_id,'System','Payment','service_entitlements',eid,rec.payment_transaction_id,
   jsonb_build_object('operation','BuildingServiceProvisioned','buildingId',item.building_id,'startsAt',p_start,'endsAt',p_end),clock_timestamp());
 END IF;
 UPDATE public.payment_provisioning_records SET status='Succeeded',provisioned_at=clock_timestamp(),last_error=NULL,
  lease_token=NULL,lease_until=NULL,updated_at=clock_timestamp() WHERE id=rec.id;
 RETURN eid;
END $$;
GRANT SELECT,UPDATE ON payment_provisioning_records TO fet3d_payos_ledger_owner;
GRANT SELECT,INSERT ON service_entitlements TO fet3d_payos_ledger_owner;
GRANT SELECT ON buildings TO fet3d_payos_ledger_owner;
GRANT UPDATE(id) ON buildings TO fet3d_payos_ledger_owner;
DO $ownership$
DECLARE had_create boolean:=has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE');
BEGIN
 GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
 ALTER FUNCTION claim_payos_provisioning_context(uuid,uuid) OWNER TO fet3d_payos_ledger_owner;
 ALTER FUNCTION finalize_payos_provisioning(uuid,uuid,timestamptz,timestamptz) OWNER TO fet3d_payos_ledger_owner;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner;END IF;
END $ownership$;
REVOKE ALL ON FUNCTION claim_payos_provisioning_context(uuid,uuid),finalize_payos_provisioning(uuid,uuid,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION claim_payos_provisioning_context(uuid,uuid),finalize_payos_provisioning(uuid,uuid,timestamptz,timestamptz) TO fet3d_payos_webhook_executor;
