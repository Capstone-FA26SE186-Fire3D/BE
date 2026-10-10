-- Forward-only expansion. Existing operations retain the legacy provisioning path.
CREATE EXTENSION IF NOT EXISTS btree_gist;
DO $preflight$ BEGIN
 IF EXISTS(SELECT 1 FROM public.service_entitlements a JOIN public.service_entitlements b
  ON a.building_id=b.building_id AND a.id<b.id AND a.payment_transaction_id IS NOT NULL AND b.payment_transaction_id IS NOT NULL
  AND a.status IN('Active','Expired','Suspended') AND b.status IN('Active','Expired','Suspended')
  AND tstzrange(a.starts_at,a.ends_at,'[)') && tstzrange(b.starts_at,b.ends_at,'[)')) THEN
  RAISE EXCEPTION 'Billing period preflight failed: overlapping paid service history requires operator review; no history was changed';
 END IF;
END $preflight$;
ALTER TABLE service_entitlements ADD COLUMN IF NOT EXISTS commercial_version integer NOT NULL DEFAULT 1,
 ADD COLUMN IF NOT EXISTS learner_limit integer;
ALTER TABLE service_entitlements ALTER COLUMN commercial_version SET DEFAULT 1;
ALTER TABLE service_entitlements ADD CONSTRAINT entitlement_v7_capacity CHECK(commercial_version IN(1,7) AND
 (commercial_version<>7 OR (learner_limit IS NOT NULL AND learner_limit>0)));
CREATE FUNCTION guard_entitlement_commercial_snapshot() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.commercial_version IS DISTINCT FROM OLD.commercial_version OR NEW.learner_limit IS DISTINCT FROM OLD.learner_limit THEN
  RAISE EXCEPTION 'Entitlement capacity snapshot is immutable' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER entitlement_commercial_snapshot BEFORE UPDATE ON service_entitlements FOR EACH ROW EXECUTE FUNCTION guard_entitlement_commercial_snapshot();
CREATE TABLE billing_service_reservations(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),checkout_id uuid NOT NULL REFERENCES billing_checkout_operations(id),
 quotation_item_id uuid NOT NULL REFERENCES quotation_building_items(id),
 building_id uuid NOT NULL REFERENCES buildings(id),organization_id uuid NOT NULL REFERENCES organizations(id),
 starts_at timestamptz NOT NULL,ends_at timestamptz NOT NULL,status text NOT NULL CHECK(status IN('Reserved','Consumed','Released')),
 created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(checkout_id,quotation_item_id),CHECK(ends_at>starts_at),
 EXCLUDE USING gist(building_id WITH =,tstzrange(starts_at,ends_at,'[)') WITH &&) WHERE(status IN('Reserved','Consumed')));
CREATE TABLE billing_ai_quota_grants(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),organization_id uuid NOT NULL REFERENCES organizations(id),
 policy_version_id uuid NOT NULL REFERENCES billing_quota_policy_versions(id),quota_unit text NOT NULL,
 quota_units integer NOT NULL CHECK(quota_units>0),starts_at timestamptz NOT NULL,ends_at timestamptz NOT NULL CHECK(ends_at>starts_at),
 quotation_item_id uuid NOT NULL REFERENCES quotation_building_items(id),payment_transaction_id uuid NOT NULL REFERENCES payment_transactions(id),
 entitlement_id uuid NOT NULL REFERENCES service_entitlements(id),provisioning_key text NOT NULL UNIQUE,
 rollover text NOT NULL DEFAULT 'None' CHECK(rollover='None'),created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(quotation_item_id,payment_transaction_id));
CREATE FUNCTION immutable_billing_quota_grant() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'Quota grant provenance is immutable' USING ERRCODE='23514'; END $$;
CREATE TRIGGER billing_quota_grant_immutable BEFORE UPDATE OR DELETE ON billing_ai_quota_grants FOR EACH ROW EXECUTE FUNCTION immutable_billing_quota_grant();
CREATE FUNCTION validate_billing_quota_grant() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM public.quotation_building_items i JOIN public.quotations q ON q.id=i.quotation_id
  JOIN public.service_entitlements e ON e.id=NEW.entitlement_id AND e.quotation_item_id=i.id
  JOIN public.payment_transactions tx ON tx.id=NEW.payment_transaction_id AND tx.status='Applied'
  JOIN public.billing_quota_policy_versions p ON p.id=i.ai_policy_version_id
  WHERE i.id=NEW.quotation_item_id AND i.commercial_version=7 AND q.organization_id=NEW.organization_id AND e.organization_id=NEW.organization_id
   AND e.payment_transaction_id=tx.id AND i.ai_quota_units=NEW.quota_units AND i.ai_quota_unit=NEW.quota_unit
   AND i.ai_policy_version_id=NEW.policy_version_id AND i.starts_at=NEW.starts_at AND i.ends_at=NEW.ends_at
   AND e.starts_at=NEW.starts_at AND e.ends_at=NEW.ends_at AND p.quota_unit=NEW.quota_unit
   AND p.effective_from<=NEW.starts_at AND (p.effective_until IS NULL OR p.effective_until>=NEW.ends_at)
   AND NEW.provisioning_key='quota:service:'||i.id::text||':'||tx.id::text)
 THEN RAISE EXCEPTION 'Quota grant must match immutable payment/quotation provenance' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER billing_quota_grant_provenance BEFORE INSERT ON billing_ai_quota_grants FOR EACH ROW EXECUTE FUNCTION validate_billing_quota_grant();
ALTER TABLE billing_service_reservations ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing_ai_quota_grants ENABLE ROW LEVEL SECURITY;
CREATE POLICY billing_service_reservation_ledger ON billing_service_reservations TO fet3d_payos_ledger_owner USING(true) WITH CHECK(true);
CREATE POLICY billing_ai_quota_grant_ledger ON billing_ai_quota_grants TO fet3d_payos_ledger_owner USING(true) WITH CHECK(true);
CREATE POLICY billing_quota_policy_ledger ON billing_quota_policy_versions TO fet3d_payos_ledger_owner USING(true);
GRANT SELECT,INSERT,UPDATE ON billing_service_reservations TO fet3d_payos_ledger_owner;
GRANT SELECT,INSERT ON billing_ai_quota_grants TO fet3d_payos_ledger_owner;
REVOKE ALL ON billing_service_reservations,billing_ai_quota_grants FROM PUBLIC,fire3d_api,fet3d_payos_request_executor,fet3d_payos_webhook_executor;
DO $roles$ DECLARE r text; BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN EXECUTE format('REVOKE ALL ON public.billing_service_reservations,public.billing_ai_quota_grants FROM %I',r); END IF;
 END LOOP;
END $roles$;

CREATE FUNCTION reserve_payos_service_periods(p_checkout uuid,p_actor uuid,p_family uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE op public.billing_checkout_operations%ROWTYPE; q public.quotations%ROWTYPE; item public.quotation_building_items%ROWTYPE;
BEGIN
 PERFORM pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0));
 PERFORM pg_advisory_xact_lock(hashtextextended('fire3d:auth:'||p_actor,0));
 SELECT * INTO op FROM public.billing_checkout_operations WHERE id=p_checkout;
 SELECT * INTO q FROM public.quotations WHERE id=op.quotation_id;
 IF op.actor_id IS DISTINCT FROM p_actor OR op.session_family_id IS DISTINCT FROM p_family OR q.commercial_version<>7 OR q.status<>'Accepted'
  OR q.valid_until<=clock_timestamp() OR NOT EXISTS(SELECT 1 FROM public.users u JOIN public.organizations o ON o.id=u.organization_id
   WHERE u.id=p_actor AND u.role='OrganizationUser' AND u.organization_id=q.organization_id AND u.is_active AND u.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL)
  OR NOT EXISTS(SELECT 1 FROM public.auth_refresh_tokens t WHERE t.user_id=p_actor AND t.family_id=p_family AND t.revoked_at IS NULL AND t.consumed_at IS NULL AND t.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_INVALID'; END IF;
 PERFORM b.id FROM public.buildings b JOIN public.quotation_building_items i ON i.building_id=b.id WHERE i.quotation_id=q.id ORDER BY b.id FOR UPDATE OF b;
 FOR item IN SELECT * FROM public.quotation_building_items WHERE quotation_id=q.id ORDER BY building_id LOOP
  IF item.starts_at IS NULL OR item.ends_at IS NULL OR item.starts_at<=clock_timestamp() OR item.commercial_version<>7
   OR NOT EXISTS(SELECT 1 FROM public.buildings WHERE id=item.building_id AND organization_id=q.organization_id AND is_active AND deleted_at IS NULL)
  THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_INVALID'; END IF;
  IF EXISTS(SELECT 1 FROM public.billing_service_reservations r WHERE r.checkout_id=p_checkout AND r.quotation_item_id=item.id AND r.status='Reserved') THEN CONTINUE; END IF;
  IF EXISTS(SELECT 1 FROM public.service_entitlements e WHERE e.building_id=item.building_id AND e.payment_transaction_id IS NOT NULL
   AND e.status IN('Active','Expired','Suspended') AND tstzrange(e.starts_at,e.ends_at,'[)') && tstzrange(item.starts_at,item.ends_at,'[)')) THEN
   RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_RESERVED'; END IF;
  BEGIN
   INSERT INTO public.billing_service_reservations(checkout_id,quotation_item_id,building_id,organization_id,starts_at,ends_at,status)
    VALUES(p_checkout,item.id,item.building_id,q.organization_id,item.starts_at,item.ends_at,'Reserved');
  EXCEPTION WHEN exclusion_violation THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_RESERVED'; END;
 END LOOP;
END $$;

CREATE FUNCTION release_confirmed_payos_reservations() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.status IN('Cancelled','Expired') AND NEW.provider_result->>'Status'=NEW.status
  AND coalesce((NEW.provider_result->>'AmountPaid')::bigint,0)=0
  AND NOT EXISTS(SELECT 1 FROM public.payos_payment_requests WHERE quotation_id=NEW.quotation_id AND status='Paid') THEN
  UPDATE public.billing_service_reservations SET status='Released',updated_at=clock_timestamp() WHERE checkout_id=NEW.id AND status='Reserved';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER release_confirmed_payos_reservations AFTER UPDATE OF status ON billing_checkout_operations
 FOR EACH ROW EXECUTE FUNCTION release_confirmed_payos_reservations();

CREATE OR REPLACE FUNCTION claim_payos_provisioning_context(p_line uuid,p_lease uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE rec public.payment_provisioning_records%ROWTYPE; item public.quotation_building_items%ROWTYPE;
 req public.payos_payment_requests%ROWTYPE; b public.buildings%ROWTYPE; last_end timestamptz; eid uuid;
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
 SELECT * INTO item FROM public.quotation_building_items WHERE id=rec.quotation_item_id;
 SELECT * INTO req FROM public.payos_payment_requests WHERE paid_transaction_id=rec.payment_transaction_id;
 IF req.status IS DISTINCT FROM 'Paid' OR req.paid_at IS NULL THEN RAISE EXCEPTION 'PAYOS_PAYMENT_NOT_APPLIED'; END IF;
 SELECT id INTO eid FROM public.service_entitlements WHERE provisioning_key=rec.provisioning_key;
 -- Existing provenance wins over expiry/lifecycle checks; recovery never grants twice.
 IF eid IS NOT NULL THEN
  RETURN jsonb_build_object('Activation',coalesce(item.starts_at,req.paid_at),'LastEnd',NULL,'Months',item.service_duration_months,'ExistingId',eid);
 END IF;
 SELECT * INTO b FROM public.buildings WHERE id=item.building_id;
 IF b.organization_id IS DISTINCT FROM rec.organization_id OR NOT b.is_active OR b.deleted_at IS NOT NULL
  OR NOT EXISTS(SELECT 1 FROM public.organizations WHERE id=rec.organization_id AND is_active AND deleted_at IS NULL)
  OR NOT EXISTS(SELECT 1 FROM public.quotations WHERE id=rec.quotation_id AND billing_purpose='BuildingService') THEN RAISE EXCEPTION 'PAYOS_BUILDING_UNAVAILABLE'; END IF;
 IF item.commercial_version=7 THEN
  IF item.ends_at<=clock_timestamp() OR req.paid_at>item.starts_at OR req.paid_at>(SELECT valid_until FROM public.quotations WHERE id=rec.quotation_id)
   OR NOT EXISTS(SELECT 1 FROM public.billing_service_reservations r JOIN public.billing_checkout_operations op ON op.id=r.checkout_id
    WHERE op.payment_request_id=req.id AND r.quotation_item_id=item.id AND r.status='Reserved' AND r.starts_at=item.starts_at AND r.ends_at=item.ends_at)
   OR EXISTS(SELECT 1 FROM public.service_entitlements e WHERE e.building_id=b.id AND e.payment_transaction_id IS NOT NULL AND e.status IN('Active','Expired','Suspended')
    AND tstzrange(e.starts_at,e.ends_at,'[)') && tstzrange(item.starts_at,item.ends_at,'[)'))
  THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_NEEDS_RECONCILE'; END IF;
  RETURN jsonb_build_object('Activation',item.starts_at,'LastEnd',NULL,'Months',item.service_duration_months,'ExistingId',NULL);
 END IF;
 SELECT max(ends_at) INTO last_end FROM public.service_entitlements WHERE building_id=b.id AND payment_transaction_id IS NOT NULL AND status IN('Active','Expired','Suspended');
 IF item.purchase_action='New' AND last_end>req.paid_at THEN RAISE EXCEPTION 'PAYOS_NEW_ALREADY_ENTITLED'; END IF;
 RETURN jsonb_build_object('Activation',req.paid_at,'LastEnd',CASE WHEN item.purchase_action='Renewal' THEN last_end ELSE NULL END,'Months',item.service_duration_months,'ExistingId',NULL);
END $$;

CREATE OR REPLACE FUNCTION finalize_payos_provisioning(p_line uuid,p_lease uuid,p_start timestamptz,p_end timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context jsonb; rec public.payment_provisioning_records%ROWTYPE; item public.quotation_building_items%ROWTYPE;
 req public.payos_payment_requests%ROWTYPE; eid uuid; expected timestamptz; gid uuid;
BEGIN
 context:=public.claim_payos_provisioning_context(p_line,p_lease);
 SELECT * INTO rec FROM public.payment_provisioning_records WHERE id=p_line;
 SELECT * INTO item FROM public.quotation_building_items WHERE id=rec.quotation_item_id;
 SELECT * INTO req FROM public.payos_payment_requests WHERE paid_transaction_id=rec.payment_transaction_id;
 eid:=(context->>'ExistingId')::uuid;
 IF eid IS NULL THEN
  expected:=greatest((context->>'Activation')::timestamptz,(context->>'LastEnd')::timestamptz);
  IF p_start IS DISTINCT FROM expected OR p_end IS DISTINCT FROM ((expected AT TIME ZONE 'UTC')+make_interval(months=>item.service_duration_months)) AT TIME ZONE 'UTC'
   OR (item.commercial_version=7 AND (p_start IS DISTINCT FROM item.starts_at OR p_end IS DISTINCT FROM item.ends_at)) THEN RAISE EXCEPTION 'PAYOS_PERIOD_MISMATCH'; END IF;
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
  UPDATE public.billing_service_reservations SET status='Consumed',updated_at=clock_timestamp() WHERE quotation_item_id=item.id AND status='Reserved';
  INSERT INTO public.audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
  VALUES(gen_random_uuid(),rec.organization_id,'System','Payment','service_entitlements',eid,rec.payment_transaction_id,
   jsonb_build_object('operation','BuildingServiceProvisioned','buildingId',item.building_id,'startsAt',p_start,'endsAt',p_end,'learnerLimit',item.learner_limit,'commercialVersion',item.commercial_version),clock_timestamp());
 END IF;
 UPDATE public.payment_provisioning_records SET status='Succeeded',provisioned_at=clock_timestamp(),last_error=NULL,lease_token=NULL,lease_until=NULL,updated_at=clock_timestamp() WHERE id=rec.id;
 RETURN eid;
END $$;
DO $ownership$ DECLARE had_create boolean:=has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE'); BEGIN
 GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
 ALTER FUNCTION reserve_payos_service_periods(uuid,uuid,uuid) OWNER TO fet3d_payos_ledger_owner;
 ALTER FUNCTION release_confirmed_payos_reservations() OWNER TO fet3d_payos_ledger_owner;
 ALTER FUNCTION claim_payos_provisioning_context(uuid,uuid) OWNER TO fet3d_payos_ledger_owner;
 ALTER FUNCTION finalize_payos_provisioning(uuid,uuid,timestamptz,timestamptz) OWNER TO fet3d_payos_ledger_owner;
 IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner; END IF;
END $ownership$;
REVOKE ALL ON FUNCTION reserve_payos_service_periods(uuid,uuid,uuid),release_confirmed_payos_reservations() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION reserve_payos_service_periods(uuid,uuid,uuid) TO fire3d_api;
