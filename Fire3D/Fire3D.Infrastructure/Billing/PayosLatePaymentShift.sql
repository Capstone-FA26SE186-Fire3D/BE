-- BE#241: PayOS money verified after the planned service start (order 10000025: credited 1 s after the payment link expired)
-- provisions the period from the payment time, same length, instead of waiting for a reconciliation that always failed again.
-- Later than planned, never overlapping: the moved period keeps the checkout's reservation and must not overlap paid service or
-- another reservation of the Building. The AI quota grant follows the provisioned period. Records already in NeedsReconcile are
-- provisioned on the next attempt (admin reconcile). Bodies are the live ones from BillingQuotationCancel / BillingV7UpgradeTopUp.
CREATE TEMP TABLE billing_late_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_payos_ledger_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_payos_ledger_owner','SET') OR NOT pg_has_role(current_user,'fet3d_payos_ledger_owner','USAGE');
 INSERT INTO billing_late_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_payos_ledger_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
END $$;

CREATE OR REPLACE FUNCTION public.claim_payos_provisioning_context(p_line uuid, p_lease uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE rec public.payment_provisioning_records%ROWTYPE; item public.quotation_building_items%ROWTYPE;
 req public.payos_payment_requests%ROWTYPE; b public.buildings%ROWTYPE; last_end timestamptz; eid uuid; q public.quotations%ROWTYPE;
 t public.quotation_topup_items%ROWTYPE; e public.service_entitlements%ROWTYPE; activation timestamptz; finish timestamptz;
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
  -- BE#241: verified money that arrives after the planned start (a late bank credit, a payment after the deadline) moves the
  -- period to the payment time with the same length instead of waiting for reconciliation; the planned period is kept otherwise.
  -- The moved period must still hold this checkout's reservation and overlap no paid service, no other reservation of the
  -- Building and stay inside the quota policy; otherwise the line waits for reconciliation as before. Nothing is granted twice.
  activation:=greatest(item.starts_at,req.paid_at);
  finish:=CASE WHEN activation=item.starts_at THEN item.ends_at
   ELSE ((activation AT TIME ZONE 'UTC')+make_interval(months=>item.service_duration_months)) AT TIME ZONE 'UTC' END;
  IF finish<=clock_timestamp()
   OR NOT EXISTS(SELECT 1 FROM public.billing_service_reservations r JOIN public.billing_checkout_operations op ON op.id=r.checkout_id
    WHERE op.payment_request_id=req.id AND r.quotation_item_id=item.id AND r.status='Reserved' AND r.starts_at=item.starts_at AND r.ends_at=item.ends_at)
   OR EXISTS(SELECT 1 FROM public.service_entitlements e2 WHERE e2.building_id=b.id AND e2.payment_transaction_id IS NOT NULL AND e2.status IN('Active','Expired','Suspended')
    AND tstzrange(e2.starts_at,e2.ends_at,'[)') && tstzrange(activation,finish,'[)'))
   OR EXISTS(SELECT 1 FROM public.billing_service_reservations r2 WHERE r2.building_id=b.id AND r2.quotation_item_id<>item.id AND r2.status IN('Reserved','Consumed')
    AND tstzrange(r2.starts_at,r2.ends_at,'[)') && tstzrange(activation,finish,'[)'))
   OR (item.ai_quota_units>0 AND NOT EXISTS(SELECT 1 FROM public.billing_quota_policy_versions p WHERE p.id=item.ai_policy_version_id
    AND p.effective_from<=activation AND (p.effective_until IS NULL OR p.effective_until>=finish)))
  THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_NEEDS_RECONCILE'; END IF;
  RETURN jsonb_build_object('Activation',activation,'LastEnd',NULL,'Months',item.service_duration_months,'ExistingId',NULL);
 END IF;
 SELECT max(ends_at) INTO last_end FROM public.service_entitlements WHERE building_id=b.id AND payment_transaction_id IS NOT NULL AND status IN('Active','Expired','Suspended');
 IF item.purchase_action='New' AND last_end>req.paid_at THEN RAISE EXCEPTION 'PAYOS_NEW_ALREADY_ENTITLED'; END IF;
 RETURN jsonb_build_object('Activation',req.paid_at,'LastEnd',CASE WHEN item.purchase_action='Renewal' THEN last_end ELSE NULL END,'Months',item.service_duration_months,'ExistingId',NULL);
END $function$;

CREATE OR REPLACE FUNCTION public.finalize_payos_provisioning(p_line uuid, p_lease uuid, p_start timestamp with time zone, p_end timestamp with time zone)
 RETURNS uuid
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE context jsonb; rec public.payment_provisioning_records%ROWTYPE; item public.quotation_building_items%ROWTYPE;
 req public.payos_payment_requests%ROWTYPE; eid uuid; expected timestamptz; gid uuid; q public.quotations%ROWTYPE; t public.quotation_topup_items%ROWTYPE;
 e public.service_entitlements%ROWTYPE; uid uuid;
BEGIN
 context:=public.claim_payos_provisioning_context(p_line,p_lease);
 SELECT * INTO rec FROM public.payment_provisioning_records WHERE id=p_line;
 SELECT * INTO q FROM public.quotations WHERE id=rec.quotation_id;
 SELECT * INTO req FROM public.payos_payment_requests WHERE paid_transaction_id=rec.payment_transaction_id;
 eid:=(context->>'ExistingId')::uuid;
 IF eid IS NULL AND q.billing_purpose='AIQuotaTopUp' THEN
  SELECT * INTO t FROM public.quotation_topup_items WHERE id=rec.quotation_item_id;
  IF p_start IS DISTINCT FROM t.starts_at THEN RAISE EXCEPTION 'PAYOS_PERIOD_MISMATCH'; END IF;
  -- Top-up creates only a grant: no entitlement, no Building period change.
  INSERT INTO public.billing_ai_quota_grants(organization_id,policy_version_id,quota_unit,quota_units,starts_at,ends_at,payment_transaction_id,provisioning_key,source_kind,topup_item_id)
  VALUES(rec.organization_id,t.policy_version_id,t.quota_unit,t.quota_units,t.starts_at,t.ends_at,rec.payment_transaction_id,'quota:'||rec.provisioning_key,'TopUp',t.id) RETURNING id INTO eid;
  INSERT INTO public.audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),rec.organization_id,'System','Grant','billing_ai_quota_grants',eid,rec.payment_transaction_id,
   jsonb_build_object('sourceKind','TopUp','quotaUnits',t.quota_units,'quotaUnit',t.quota_unit,'startsAt',t.starts_at,'endsAt',t.ends_at,'policyVersionId',t.policy_version_id,'rollover','None'),clock_timestamp());
 ELSIF eid IS NULL THEN
  SELECT * INTO item FROM public.quotation_building_items WHERE id=rec.quotation_item_id;
  IF item.purchase_action='Upgrade' THEN
   SELECT * INTO e FROM public.service_entitlements WHERE id=item.upgrade_entitlement_id;
   IF p_start IS DISTINCT FROM item.starts_at THEN RAISE EXCEPTION 'PAYOS_PERIOD_MISMATCH'; END IF;
   INSERT INTO public.billing_entitlement_upgrades(entitlement_id,capacity_revision,previous_learner_limit,learner_limit,additional_quota_units,effective_from,quotation_item_id,payment_transaction_id,provisioning_key)
   VALUES(e.id,item.upgrade_base_capacity_revision+1,item.upgrade_previous_learner_limit,item.learner_limit,item.ai_quota_units,item.starts_at,item.id,rec.payment_transaction_id,rec.provisioning_key) RETURNING id INTO uid;
   IF item.ai_quota_units>0 THEN
    INSERT INTO public.billing_ai_quota_grants(organization_id,policy_version_id,quota_unit,quota_units,starts_at,ends_at,quotation_item_id,payment_transaction_id,entitlement_id,provisioning_key,source_kind,upgrade_id)
    VALUES(rec.organization_id,item.ai_policy_version_id,item.ai_quota_unit,item.ai_quota_units,item.starts_at,e.ends_at,item.id,rec.payment_transaction_id,e.id,'quota:'||rec.provisioning_key,'Upgrade',uid) RETURNING id INTO gid;
   END IF;
   UPDATE public.billing_upgrade_reservations SET status='Consumed',updated_at=clock_timestamp() WHERE quotation_item_id=item.id AND status='Reserved';
   INSERT INTO public.audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
   VALUES(gen_random_uuid(),rec.organization_id,'System','Payment','billing_entitlement_upgrades',uid,rec.payment_transaction_id,
    jsonb_build_object('operation','BuildingServiceUpgraded','entitlementId',e.id,'capacityRevision',item.upgrade_base_capacity_revision+1,'learnerLimit',item.learner_limit,
     'previousLearnerLimit',item.upgrade_previous_learner_limit,'effectiveFrom',item.starts_at,'additionalQuotaUnits',item.ai_quota_units,'quotaGrantId',gid),clock_timestamp());
   eid:=uid;
  ELSE
   expected:=greatest((context->>'Activation')::timestamptz,(context->>'LastEnd')::timestamptz);
   IF p_start IS DISTINCT FROM expected OR p_end IS DISTINCT FROM ((expected AT TIME ZONE 'UTC')+make_interval(months=>item.service_duration_months)) AT TIME ZONE 'UTC'
    OR (item.commercial_version=7 AND p_start IS DISTINCT FROM greatest(item.starts_at,req.paid_at)) THEN RAISE EXCEPTION 'PAYOS_PERIOD_MISMATCH'; END IF;
   INSERT INTO public.service_entitlements(id,organization_id,building_id,service_package_id,quotation_id,quotation_item_id,payment_transaction_id,provisioning_key,status,
    starts_at,ends_at,price_snapshot,terms_snapshot,playtest_units_granted,playtest_units_used,created_by,created_at,commercial_version,learner_limit)
   VALUES(gen_random_uuid(),rec.organization_id,item.building_id,item.service_package_id,rec.quotation_id,item.id,rec.payment_transaction_id,rec.provisioning_key,'Active',
    p_start,p_end,item.price_snapshot,item.terms_snapshot,0,0,req.requested_by,clock_timestamp(),item.commercial_version,item.learner_limit) RETURNING id INTO eid;
   IF item.commercial_version=7 AND item.ai_quota_units>0 THEN
    INSERT INTO public.billing_ai_quota_grants(organization_id,policy_version_id,quota_unit,quota_units,starts_at,ends_at,quotation_item_id,payment_transaction_id,entitlement_id,provisioning_key)
    VALUES(rec.organization_id,item.ai_policy_version_id,item.ai_quota_unit,item.ai_quota_units,p_start,p_end,item.id,rec.payment_transaction_id,eid,'quota:'||rec.provisioning_key) RETURNING id INTO gid;
    INSERT INTO public.audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
    VALUES(gen_random_uuid(),rec.organization_id,'System','Grant','billing_ai_quota_grants',gid,rec.payment_transaction_id,
     jsonb_build_object('quotaUnits',item.ai_quota_units,'quotaUnit',item.ai_quota_unit,'startsAt',p_start,'endsAt',p_end,'policyVersionId',item.ai_policy_version_id,'rollover','None'),clock_timestamp());
   END IF;
   -- The reservation follows the provisioned period; the exclusion constraint refuses a moved period another reservation holds.
   BEGIN
    UPDATE public.billing_service_reservations SET status='Consumed',starts_at=p_start,ends_at=p_end,updated_at=clock_timestamp() WHERE quotation_item_id=item.id AND status='Reserved';
   EXCEPTION WHEN exclusion_violation THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_NEEDS_RECONCILE'; END;
   INSERT INTO public.audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
   VALUES(gen_random_uuid(),rec.organization_id,'System','Payment','service_entitlements',eid,rec.payment_transaction_id,
    jsonb_build_object('operation','BuildingServiceProvisioned','buildingId',item.building_id,'startsAt',p_start,'endsAt',p_end,'learnerLimit',item.learner_limit,'commercialVersion',item.commercial_version)
     ||CASE WHEN item.commercial_version=7 AND p_start<>item.starts_at THEN jsonb_build_object('periodShiftedFrom',item.starts_at,'paidAt',req.paid_at) ELSE '{}'::jsonb END,clock_timestamp());
  END IF;
 END IF;
 UPDATE public.payment_provisioning_records SET status='Succeeded',provisioned_at=clock_timestamp(),last_error=NULL,lease_token=NULL,lease_until=NULL,updated_at=clock_timestamp() WHERE id=rec.id;
 RETURN eid;
END $function$;

CREATE OR REPLACE FUNCTION public.validate_billing_quota_grant()
 RETURNS trigger
 LANGUAGE plpgsql
 SET search_path TO 'pg_catalog'
AS $function$
BEGIN
 IF NEW.source_kind='BuildingService' AND NOT EXISTS(SELECT 1 FROM public.quotation_building_items i JOIN public.quotations q ON q.id=i.quotation_id
  JOIN public.service_entitlements e ON e.id=NEW.entitlement_id AND e.quotation_item_id=i.id
  JOIN public.payment_transactions tx ON tx.id=NEW.payment_transaction_id AND tx.status='Applied'
  JOIN public.billing_quota_policy_versions p ON p.id=i.ai_policy_version_id
  WHERE i.id=NEW.quotation_item_id AND i.commercial_version=7 AND i.purchase_action IN('New','Renewal') AND q.organization_id=NEW.organization_id AND e.organization_id=NEW.organization_id
   AND e.payment_transaction_id=tx.id AND i.ai_quota_units=NEW.quota_units AND i.ai_quota_unit=NEW.quota_unit
   AND i.ai_policy_version_id=NEW.policy_version_id AND NEW.starts_at>=i.starts_at
   AND ((NEW.starts_at=i.starts_at AND NEW.ends_at=i.ends_at) OR NEW.ends_at=((NEW.starts_at AT TIME ZONE 'UTC')+make_interval(months=>i.service_duration_months)) AT TIME ZONE 'UTC')
   AND e.starts_at=NEW.starts_at AND e.ends_at=NEW.ends_at AND p.quota_unit=NEW.quota_unit
   AND p.effective_from<=NEW.starts_at AND (p.effective_until IS NULL OR p.effective_until>=NEW.ends_at)
   AND NEW.provisioning_key='quota:service:'||i.id::text||':'||tx.id::text)
 THEN RAISE EXCEPTION 'Quota grant must match immutable payment/quotation provenance' USING ERRCODE='23514'; END IF;
 IF NEW.source_kind='Upgrade' AND NOT EXISTS(SELECT 1 FROM public.billing_entitlement_upgrades u JOIN public.quotation_building_items i ON i.id=u.quotation_item_id
  JOIN public.service_entitlements e ON e.id=u.entitlement_id JOIN public.payment_transactions tx ON tx.id=u.payment_transaction_id AND tx.status='Applied'
  JOIN public.billing_quota_policy_versions p ON p.id=i.ai_policy_version_id
  WHERE u.id=NEW.upgrade_id AND i.id=NEW.quotation_item_id AND e.id=NEW.entitlement_id AND tx.id=NEW.payment_transaction_id AND e.organization_id=NEW.organization_id
   AND u.additional_quota_units=NEW.quota_units AND i.ai_quota_units=NEW.quota_units AND i.ai_quota_unit=NEW.quota_unit AND i.ai_policy_version_id=NEW.policy_version_id
   AND NEW.starts_at=u.effective_from AND NEW.ends_at=e.ends_at AND p.effective_from<=NEW.starts_at AND (p.effective_until IS NULL OR p.effective_until>=NEW.ends_at)
   AND NEW.provisioning_key='quota:'||u.provisioning_key)
 THEN RAISE EXCEPTION 'Upgrade quota grant must match immutable upgrade provenance' USING ERRCODE='23514'; END IF;
 IF NEW.source_kind='TopUp' AND NOT EXISTS(SELECT 1 FROM public.quotation_topup_items t JOIN public.quotations q ON q.id=t.quotation_id AND q.billing_purpose='AIQuotaTopUp'
  JOIN public.payos_payment_requests r ON r.quotation_id=q.id AND r.status='Paid' AND r.paid_transaction_id=NEW.payment_transaction_id
  JOIN public.payment_transactions tx ON tx.id=NEW.payment_transaction_id AND tx.status='Applied'
  WHERE t.id=NEW.topup_item_id AND t.organization_id=NEW.organization_id AND q.organization_id=NEW.organization_id
   AND t.quota_units=NEW.quota_units AND t.quota_unit=NEW.quota_unit AND t.policy_version_id=NEW.policy_version_id
   AND t.starts_at=NEW.starts_at AND t.ends_at=NEW.ends_at AND NEW.provisioning_key='quota:topup:'||t.id::text||':'||tx.id::text)
 THEN RAISE EXCEPTION 'Top-up quota grant must match immutable payment/quotation provenance' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $function$;

DO $grants$ DECLARE s record;BEGIN
 SELECT * INTO s FROM billing_late_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner;END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_payos_ledger_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_payos_ledger_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $grants$;
