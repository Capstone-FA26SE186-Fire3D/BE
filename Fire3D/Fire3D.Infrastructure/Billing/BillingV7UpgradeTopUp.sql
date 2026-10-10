-- Forward-only: Building upgrade, paid AI top-up, quota ledger allocations and learner seats.
-- Existing quotations, entitlements, grants and provisioning records keep their stored values.
DO $preflight$ BEGIN
 IF EXISTS(SELECT 1 FROM public.quotation_building_items WHERE purchase_action NOT IN('New','Renewal')) THEN
  RAISE EXCEPTION 'Billing upgrade preflight: unknown purchase actions require operator review'; END IF;
 IF EXISTS(SELECT 1 FROM public.quotations WHERE billing_purpose NOT IN('BuildingService','AIUsage')) THEN
  RAISE EXCEPTION 'Billing upgrade preflight: unknown billing purposes require operator review'; END IF;
END $preflight$;

CREATE TEMP TABLE billing_upgrade_permissions(original_set boolean,original_inherit boolean,changed boolean,had_create boolean) ON COMMIT DROP;
DO $$ DECLARE os boolean;oi boolean;c boolean;BEGIN
 SELECT set_option,inherit_option INTO os,oi FROM pg_auth_members WHERE roleid='fet3d_payos_ledger_owner'::regrole AND member=current_user::regrole;
 c:=NOT pg_has_role(current_user,'fet3d_payos_ledger_owner','SET') OR NOT pg_has_role(current_user,'fet3d_payos_ledger_owner','USAGE');
 INSERT INTO billing_upgrade_permissions VALUES(os,oi,c,has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE'));
 IF c THEN EXECUTE format('GRANT fet3d_payos_ledger_owner TO %I WITH SET TRUE,INHERIT TRUE',current_user);END IF;
 GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
END $$;

-- 1. Upgrade lines: one-time price fixed by Admin, same entitlement, period and consumed seats.
ALTER TABLE quotation_building_items DROP CONSTRAINT IF EXISTS check_quotation_item_action;
ALTER TABLE quotation_building_items ADD CONSTRAINT check_quotation_item_action CHECK(purchase_action IN('New','Renewal','Upgrade'));
-- Columns may already exist when a test baseline is created from the EF model; constraints are always added here.
ALTER TABLE quotation_building_items
 ADD COLUMN IF NOT EXISTS pricing_basis text NOT NULL DEFAULT 'Monthly',
 ADD COLUMN IF NOT EXISTS upgrade_entitlement_id uuid,
 ADD COLUMN IF NOT EXISTS upgrade_base_capacity_revision integer,
 ADD COLUMN IF NOT EXISTS upgrade_previous_learner_limit integer;
DO $fk$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='public.quotation_building_items'::regclass AND contype='f'
  AND confrelid='public.service_entitlements'::regclass) THEN
  ALTER TABLE quotation_building_items ADD CONSTRAINT quotation_line_upgrade_entitlement_fk FOREIGN KEY(upgrade_entitlement_id) REFERENCES service_entitlements(id);
 END IF;
END $fk$;
ALTER TABLE quotation_building_items ADD CONSTRAINT quotation_line_upgrade_values CHECK(pricing_basis IN('Monthly','OneTime')
 AND (upgrade_base_capacity_revision IS NULL OR upgrade_base_capacity_revision>=0) AND (upgrade_previous_learner_limit IS NULL OR upgrade_previous_learner_limit>0));
ALTER TABLE quotation_building_items ADD CONSTRAINT quotation_line_upgrade_shape CHECK(
 (purchase_action='Upgrade')=(pricing_basis='OneTime')
 AND (purchase_action='Upgrade')=(upgrade_entitlement_id IS NOT NULL)
 AND (purchase_action<>'Upgrade' OR commercial_version=7));

CREATE TABLE billing_entitlement_upgrades(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),entitlement_id uuid NOT NULL REFERENCES service_entitlements(id),
 capacity_revision integer NOT NULL CHECK(capacity_revision>=1),previous_learner_limit integer NOT NULL,learner_limit integer NOT NULL,
 additional_quota_units integer NOT NULL CHECK(additional_quota_units>=0),effective_from timestamptz NOT NULL,
 quotation_item_id uuid NOT NULL REFERENCES quotation_building_items(id),payment_transaction_id uuid NOT NULL REFERENCES payment_transactions(id),
 provisioning_key text NOT NULL UNIQUE,created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 UNIQUE(entitlement_id,capacity_revision),UNIQUE(quotation_item_id,payment_transaction_id),CHECK(learner_limit>previous_learner_limit));
CREATE FUNCTION immutable_billing_upgrade() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'Upgrade provenance is immutable' USING ERRCODE='23514'; END $$;
CREATE TRIGGER billing_upgrade_immutable BEFORE UPDATE OR DELETE ON billing_entitlement_upgrades FOR EACH ROW EXECUTE FUNCTION immutable_billing_upgrade();

CREATE TABLE billing_upgrade_reservations(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),checkout_id uuid NOT NULL REFERENCES billing_checkout_operations(id),
 quotation_item_id uuid NOT NULL REFERENCES quotation_building_items(id),entitlement_id uuid NOT NULL REFERENCES service_entitlements(id),
 base_capacity_revision integer NOT NULL,status text NOT NULL CHECK(status IN('Reserved','Consumed','Released')),
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 UNIQUE(checkout_id,quotation_item_id));
-- Two checkouts cannot both build on the same capacity revision of one entitlement.
CREATE UNIQUE INDEX billing_upgrade_reservation_baseline ON billing_upgrade_reservations(entitlement_id,base_capacity_revision) WHERE status IN('Reserved','Consumed');

CREATE FUNCTION billing_capacity_revision(p_entitlement uuid) RETURNS integer LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT COALESCE(max(capacity_revision),0) FROM public.billing_entitlement_upgrades WHERE entitlement_id=p_entitlement
$$;
CREATE FUNCTION billing_effective_learner_limit(p_entitlement uuid,p_at timestamptz) RETURNS integer LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT COALESCE((SELECT u.learner_limit FROM public.billing_entitlement_upgrades u WHERE u.entitlement_id=e.id AND u.effective_from<=p_at ORDER BY u.capacity_revision DESC LIMIT 1),e.learner_limit)
 FROM public.service_entitlements e WHERE e.id=p_entitlement
$$;

-- 2. Paid AI quota top-up: its own purpose and line table; never extends Building service.
ALTER TABLE quotations DROP CONSTRAINT IF EXISTS check_quotation_billing_purpose;
ALTER TABLE quotations ADD CONSTRAINT check_quotation_billing_purpose CHECK(billing_purpose IN('BuildingService','AIUsage','AIQuotaTopUp'));
CREATE TABLE IF NOT EXISTS quotation_topup_items(
 id uuid PRIMARY KEY,quotation_id uuid NOT NULL UNIQUE REFERENCES quotations(id),organization_id uuid NOT NULL REFERENCES organizations(id),
 requested_quota_units integer,policy_version_id uuid REFERENCES billing_quota_policy_versions(id),
 quota_unit text,quota_units integer,amount numeric(14,2),
 starts_at timestamptz,ends_at timestamptz,line_provisioning_key text NOT NULL UNIQUE,
 created_at timestamptz NOT NULL,updated_at timestamptz NOT NULL);
ALTER TABLE quotation_topup_items ADD CONSTRAINT quotation_topup_values CHECK((requested_quota_units IS NULL OR requested_quota_units>0)
 AND (quota_units IS NULL OR quota_units>0) AND (amount IS NULL OR (amount>=0 AND amount=trunc(amount)))
 AND (ends_at IS NULL OR starts_at IS NULL OR ends_at>starts_at));
CREATE FUNCTION guard_quotation_topup_item() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE q public.quotations%ROWTYPE;
BEGIN
 SELECT * INTO q FROM public.quotations WHERE id=CASE WHEN TG_OP='DELETE' THEN OLD.quotation_id ELSE NEW.quotation_id END FOR UPDATE;
 IF q.billing_purpose IS DISTINCT FROM 'AIQuotaTopUp' THEN RAISE EXCEPTION 'Top-up line requires an AIQuotaTopUp quotation' USING ERRCODE='23514'; END IF;
 IF q.status<>'Draft' THEN RAISE EXCEPTION 'Top-up lines change only while Draft' USING ERRCODE='23514'; END IF;
 IF TG_OP<>'DELETE' AND (NEW.organization_id<>q.organization_id OR (TG_OP='UPDATE' AND NEW.quotation_id<>OLD.quotation_id)) THEN
  RAISE EXCEPTION 'Top-up line belongs to its quotation tenant' USING ERRCODE='23514'; END IF;
 RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
END $$;
CREATE TRIGGER quotation_topup_item_guard BEFORE INSERT OR UPDATE OR DELETE ON quotation_topup_items FOR EACH ROW EXECUTE FUNCTION guard_quotation_topup_item();

-- Issued/Accepted contract per purpose; upgrade lines keep their entitlement period instead of a fresh interval.
CREATE OR REPLACE FUNCTION validate_v7_quotation_contract() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE t public.quotation_topup_items%ROWTYPE;p public.billing_quota_policy_versions%ROWTYPE;
BEGIN
 IF TG_OP='UPDATE' AND OLD.status<>'Draft' AND OLD.commercial_version IS DISTINCT FROM NEW.commercial_version THEN
  RAISE EXCEPTION 'Quotation commercial version is immutable' USING ERRCODE='23514';
 END IF;
 IF NEW.billing_purpose='AIQuotaTopUp' AND NEW.status IN('Issued','Accepted') THEN
  SELECT * INTO t FROM public.quotation_topup_items WHERE quotation_id=NEW.id;
  SELECT * INTO p FROM public.billing_quota_policy_versions WHERE id=t.policy_version_id;
  IF t.id IS NULL OR p.id IS NULL OR t.quota_units IS NULL OR t.amount IS NULL OR t.amount<=0 OR t.starts_at IS NULL OR t.ends_at IS NULL
   OR p.audience<>'organization' OR p.policy_kind<>'quota' OR p.quota_unit<>t.quota_unit OR p.effective_from>t.starts_at OR p.effective_until<t.ends_at
   OR NEW.valid_until>t.starts_at OR NEW.quantity<>1 OR NEW.discount_amount<>0 OR NEW.subtotal_amount<>t.amount OR NEW.total_amount<>t.amount+NEW.tax_amount
   OR EXISTS(SELECT 1 FROM public.quotation_building_items WHERE quotation_id=NEW.id) THEN
   RAISE EXCEPTION 'Incomplete AI top-up quotation snapshot' USING ERRCODE='23514'; END IF;
 END IF;
 IF NEW.commercial_version=7 AND NEW.status IN('Issued','Accepted') AND EXISTS(
  SELECT 1 FROM public.quotation_building_items i LEFT JOIN public.billing_quota_policy_versions pv ON pv.id=i.ai_policy_version_id
   LEFT JOIN public.service_entitlements e ON e.id=i.upgrade_entitlement_id
  WHERE i.quotation_id=NEW.id AND (i.commercial_version<>7 OR i.starts_at IS NULL OR i.ends_at IS NULL OR NEW.valid_until>i.starts_at
   OR (i.purchase_action<>'Upgrade' AND i.ends_at IS DISTINCT FROM ((i.starts_at AT TIME ZONE 'UTC')+make_interval(months=>i.service_duration_months)) AT TIME ZONE 'UTC')
   OR (i.purchase_action='Upgrade' AND (e.id IS NULL OR e.building_id<>i.building_id OR e.organization_id<>NEW.organization_id OR i.ends_at IS DISTINCT FROM e.ends_at
     OR i.starts_at>=e.ends_at OR i.upgrade_base_capacity_revision IS NULL OR i.upgrade_previous_learner_limit IS NULL OR i.learner_limit<=i.upgrade_previous_learner_limit))
   OR (i.ai_quota_units>0 AND (pv.id IS NULL OR pv.audience<>'organization' OR pv.policy_kind<>'quota' OR pv.quota_unit<>i.ai_quota_unit OR pv.effective_from>i.starts_at OR pv.effective_until<i.ends_at)))
 ) THEN RAISE EXCEPTION 'Incomplete v7 quotation snapshot or service interval' USING ERRCODE='23514'; END IF;
 IF NEW.billing_purpose='BuildingService' AND NEW.status IN('Issued','Accepted')
  AND EXISTS(SELECT 1 FROM public.quotation_building_items WHERE quotation_id=NEW.id AND purchase_action='Upgrade')
  AND EXISTS(SELECT 1 FROM public.quotation_building_items WHERE quotation_id=NEW.id AND purchase_action<>'Upgrade') THEN
  RAISE EXCEPTION 'Upgrade lines are not mixed with New/Renewal lines' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;

-- 3. Grants: one table for every prepaid source, with provenance per source kind.
ALTER TABLE billing_ai_quota_grants
 ADD COLUMN source_kind text NOT NULL DEFAULT 'BuildingService' CHECK(source_kind IN('BuildingService','Upgrade','TopUp')),
 ADD COLUMN upgrade_id uuid REFERENCES billing_entitlement_upgrades(id),
 ADD COLUMN topup_item_id uuid REFERENCES quotation_topup_items(id),
 ALTER COLUMN quotation_item_id DROP NOT NULL,
 ALTER COLUMN entitlement_id DROP NOT NULL;
ALTER TABLE billing_ai_quota_grants ADD CONSTRAINT billing_quota_grant_source CHECK(
 (source_kind='BuildingService' AND quotation_item_id IS NOT NULL AND entitlement_id IS NOT NULL AND upgrade_id IS NULL AND topup_item_id IS NULL)
 OR (source_kind='Upgrade' AND quotation_item_id IS NOT NULL AND entitlement_id IS NOT NULL AND upgrade_id IS NOT NULL AND topup_item_id IS NULL)
 OR (source_kind='TopUp' AND topup_item_id IS NOT NULL AND quotation_item_id IS NULL AND entitlement_id IS NULL AND upgrade_id IS NULL));
ALTER TABLE billing_ai_quota_grants ADD CONSTRAINT billing_quota_grant_topup_once UNIQUE(topup_item_id,payment_transaction_id);
ALTER TABLE billing_ai_quota_grants ADD CONSTRAINT billing_quota_grant_upgrade_once UNIQUE(upgrade_id);
CREATE OR REPLACE FUNCTION validate_billing_quota_grant() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.source_kind='BuildingService' AND NOT EXISTS(SELECT 1 FROM public.quotation_building_items i JOIN public.quotations q ON q.id=i.quotation_id
  JOIN public.service_entitlements e ON e.id=NEW.entitlement_id AND e.quotation_item_id=i.id
  JOIN public.payment_transactions tx ON tx.id=NEW.payment_transaction_id AND tx.status='Applied'
  JOIN public.billing_quota_policy_versions p ON p.id=i.ai_policy_version_id
  WHERE i.id=NEW.quotation_item_id AND i.commercial_version=7 AND i.purchase_action IN('New','Renewal') AND q.organization_id=NEW.organization_id AND e.organization_id=NEW.organization_id
   AND e.payment_transaction_id=tx.id AND i.ai_quota_units=NEW.quota_units AND i.ai_quota_unit=NEW.quota_unit
   AND i.ai_policy_version_id=NEW.policy_version_id AND i.starts_at=NEW.starts_at AND i.ends_at=NEW.ends_at
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
END $$;

-- 4. Quota allocations (reserve/settle/release ledger) and learner seats; written only by later gates.
CREATE TABLE billing_ai_quota_allocations(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),grant_id uuid NOT NULL REFERENCES billing_ai_quota_grants(id),
 organization_id uuid NOT NULL REFERENCES organizations(id),request_id uuid NOT NULL,quota_unit text NOT NULL,
 reserved_units integer NOT NULL CHECK(reserved_units>0),consumed_units integer CHECK(consumed_units>=0),
 status text NOT NULL CHECK(status IN('Reserved','Settled','Released')),
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),settled_at timestamptz,
 UNIQUE(request_id,grant_id),
 CHECK((status='Reserved' AND consumed_units IS NULL AND settled_at IS NULL) OR (status='Settled' AND consumed_units IS NOT NULL AND consumed_units<=reserved_units AND settled_at IS NOT NULL)
  OR (status='Released' AND consumed_units IS NULL AND settled_at IS NOT NULL)));
CREATE INDEX billing_ai_quota_allocations_grant ON billing_ai_quota_allocations(grant_id,status);
CREATE TABLE billing_learner_seats(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),entitlement_id uuid NOT NULL REFERENCES service_entitlements(id),
 organization_id uuid NOT NULL REFERENCES organizations(id),building_id uuid NOT NULL REFERENCES buildings(id),
 trainee_id uuid NOT NULL REFERENCES users(id),first_session_id uuid NOT NULL,allocated_at timestamptz NOT NULL,
 UNIQUE(entitlement_id,trainee_id));
CREATE FUNCTION immutable_learner_seat() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'Learner seats are append-only' USING ERRCODE='23514'; END $$;
CREATE TRIGGER learner_seat_immutable BEFORE UPDATE OR DELETE ON billing_learner_seats FOR EACH ROW EXECUTE FUNCTION immutable_learner_seat();

-- 5. Enterprise request revision for If-Match status changes.
ALTER TABLE enterprise_quote_requests ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;
ALTER TABLE enterprise_quote_requests ADD CONSTRAINT enterprise_quote_revision_positive CHECK(revision>=1);

-- 6. Checkout reservation: service periods for New/Renewal, capacity baseline for Upgrade; no-op for top-up.
CREATE OR REPLACE FUNCTION reserve_payos_service_periods(p_checkout uuid,p_actor uuid,p_family uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE op public.billing_checkout_operations%ROWTYPE; q public.quotations%ROWTYPE; item public.quotation_building_items%ROWTYPE; e public.service_entitlements%ROWTYPE;
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
 IF q.billing_purpose='AIQuotaTopUp' THEN RETURN; END IF;
 PERFORM b.id FROM public.buildings b JOIN public.quotation_building_items i ON i.building_id=b.id WHERE i.quotation_id=q.id ORDER BY b.id FOR UPDATE OF b;
 FOR item IN SELECT * FROM public.quotation_building_items WHERE quotation_id=q.id ORDER BY building_id LOOP
  IF item.starts_at IS NULL OR item.ends_at IS NULL OR item.starts_at<=clock_timestamp() OR item.commercial_version<>7
   OR NOT EXISTS(SELECT 1 FROM public.buildings WHERE id=item.building_id AND organization_id=q.organization_id AND is_active AND deleted_at IS NULL)
  THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_INVALID'; END IF;
  IF item.purchase_action='Upgrade' THEN
   IF EXISTS(SELECT 1 FROM public.billing_upgrade_reservations r WHERE r.checkout_id=p_checkout AND r.quotation_item_id=item.id AND r.status='Reserved') THEN CONTINUE; END IF;
   SELECT * INTO e FROM public.service_entitlements WHERE id=item.upgrade_entitlement_id FOR UPDATE;
   IF e.id IS NULL OR e.status<>'Active' OR e.payment_transaction_id IS NULL OR e.building_id<>item.building_id OR e.organization_id<>q.organization_id OR e.ends_at<=clock_timestamp()
    OR public.billing_capacity_revision(e.id)<>item.upgrade_base_capacity_revision THEN RAISE EXCEPTION 'PAYOS_UPGRADE_BASELINE_CHANGED'; END IF;
   BEGIN
    INSERT INTO public.billing_upgrade_reservations(checkout_id,quotation_item_id,entitlement_id,base_capacity_revision,status)
     VALUES(p_checkout,item.id,e.id,item.upgrade_base_capacity_revision,'Reserved');
   EXCEPTION WHEN unique_violation THEN RAISE EXCEPTION 'PAYOS_UPGRADE_RESERVED'; END;
   CONTINUE;
  END IF;
  IF EXISTS(SELECT 1 FROM public.billing_service_reservations r WHERE r.checkout_id=p_checkout AND r.quotation_item_id=item.id AND r.status='Reserved') THEN CONTINUE; END IF;
  IF EXISTS(SELECT 1 FROM public.service_entitlements e2 WHERE e2.building_id=item.building_id AND e2.payment_transaction_id IS NOT NULL
   AND e2.status IN('Active','Expired','Suspended') AND tstzrange(e2.starts_at,e2.ends_at,'[)') && tstzrange(item.starts_at,item.ends_at,'[)')) THEN
   RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_RESERVED'; END IF;
  BEGIN
   INSERT INTO public.billing_service_reservations(checkout_id,quotation_item_id,building_id,organization_id,starts_at,ends_at,status)
    VALUES(p_checkout,item.id,item.building_id,q.organization_id,item.starts_at,item.ends_at,'Reserved');
  EXCEPTION WHEN exclusion_violation THEN RAISE EXCEPTION 'PAYOS_SERVICE_PERIOD_RESERVED'; END;
 END LOOP;
END $$;

CREATE OR REPLACE FUNCTION release_confirmed_payos_reservations() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.status IN('Cancelled','Expired') AND NEW.provider_result->>'Status'=NEW.status
  AND coalesce((NEW.provider_result->>'AmountPaid')::bigint,0)=0
  AND NOT EXISTS(SELECT 1 FROM public.payos_payment_requests WHERE quotation_id=NEW.quotation_id AND status='Paid') THEN
  UPDATE public.billing_service_reservations SET status='Released',updated_at=clock_timestamp() WHERE checkout_id=NEW.id AND status='Reserved';
  UPDATE public.billing_upgrade_reservations SET status='Released',updated_at=clock_timestamp() WHERE checkout_id=NEW.id AND status='Reserved';
 END IF;
 RETURN NEW;
END $$;

-- 7. Verified payment creates one provisioning record per Building line or top-up line.
CREATE OR REPLACE FUNCTION process_payos_inbox(p_inbox uuid,p_lease uuid) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE ev public.payos_webhook_inbox%ROWTYPE; req public.payos_payment_requests%ROWTYPE;
 op public.billing_checkout_operations%ROWTYPE; old public.payment_transactions%ROWTYPE;
 tid uuid; reason text; quote uuid;
BEGIN
 SELECT * INTO ev FROM public.payos_webhook_inbox WHERE id=p_inbox FOR UPDATE;
 IF NOT FOUND OR p_lease IS NULL OR ev.lease_until IS NULL OR ev.lease_token IS DISTINCT FROM p_lease OR ev.lease_until<=clock_timestamp()
  OR ev.verified_at IS NULL THEN RAISE EXCEPTION 'PAYOS_STALE_LEASE'; END IF;
 SELECT quotation_id INTO quote FROM public.billing_checkout_operations WHERE order_code=ev.order_code;
 IF quote IS NULL THEN
  UPDATE public.payos_webhook_inbox SET status='NeedsReconcile',last_error='PAYOS_UNKNOWN_ORDER',lease_token=NULL,lease_until=NULL,
   next_attempt_at=clock_timestamp()+interval '30 minutes' WHERE id=ev.id;RETURN NULL;
 END IF;
 -- Same order as checkout binding: operation -> quotation -> request. No identity dependency for accepted money.
 SELECT * INTO op FROM public.billing_checkout_operations WHERE order_code=ev.order_code FOR UPDATE;
 PERFORM 1 FROM public.quotations WHERE id=quote FOR UPDATE;
 SELECT * INTO req FROM public.payos_payment_requests WHERE id=op.payment_request_id FOR UPDATE;
 IF NOT FOUND THEN
  UPDATE public.payos_webhook_inbox SET status='NeedsReconcile',last_error='PAYOS_AWAITING_BIND',lease_token=NULL,lease_until=NULL,
   next_attempt_at=clock_timestamp()+make_interval(secs=>least(1800,30*power(2,least(ev.attempts-1,6)))::double precision) WHERE id=ev.id;RETURN NULL;
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('fet3d:payos:reference:'||(ev.payload->>'Reference'),0));
 SELECT * INTO old FROM public.payment_transactions WHERE provider_transaction_id=ev.payload->>'Reference';
 IF FOUND THEN
  IF old.payment_request_id<>req.id OR old.raw_payload IS DISTINCT FROM ev.payload THEN
   RAISE EXCEPTION 'PAYOS_REFERENCE_CONFLICT'; END IF;
  UPDATE public.payos_webhook_inbox SET status=old.status::text,processed_at=clock_timestamp(),last_error=old.rejection_reason,
   lease_token=NULL,lease_until=NULL WHERE id=ev.id;RETURN old.id;
 END IF;
 IF (ev.payload->>'OrderCode')::bigint IS DISTINCT FROM req.order_code
  OR (ev.payload->>'Amount')::numeric IS DISTINCT FROM req.expected_amount
  OR ev.payload->>'Currency' IS DISTINCT FROM req.expected_currency
  OR ev.payload->>'Currency'<>'VND'
  OR ev.payload->>'PaymentLinkId' IS DISTINCT FROM req.payment_link_id THEN reason:='PAYOS_SNAPSHOT_MISMATCH';
 ELSIF EXISTS(SELECT 1 FROM public.payos_payment_requests WHERE quotation_id=quote AND status='Paid') THEN
  reason:='PAYOS_QUOTATION_ALREADY_PAID';
 END IF;
 INSERT INTO public.payment_transactions(payment_request_id,webhook_event_id,provider_transaction_id,received_order_code,received_amount,received_currency,raw_payload)
 VALUES(req.id,ev.event_key,ev.payload->>'Reference',(ev.payload->>'OrderCode')::bigint,(ev.payload->>'Amount')::numeric,ev.payload->>'Currency',ev.payload) RETURNING id INTO tid;
 UPDATE public.payment_transactions SET status='Verified',signature_verified=true,signature_verified_at=ev.verified_at WHERE id=tid;
 IF reason IS NOT NULL THEN
  UPDATE public.payment_transactions SET status='Rejected',rejection_reason=reason,processed_at=clock_timestamp() WHERE id=tid;
 ELSE
  UPDATE public.payment_transactions SET status='Applied',processed_at=clock_timestamp() WHERE id=tid;
  UPDATE public.payos_payment_requests SET status='Paid',paid_transaction_id=tid,paid_at=clock_timestamp() WHERE id=req.id;
  INSERT INTO public.payment_provisioning_records(id,payment_transaction_id,quotation_id,quotation_item_id,organization_id,provisioning_key,status,attempts,created_at,updated_at,next_attempt_at)
  SELECT gen_random_uuid(),tid,quote,item.id,req.organization_id,'service:'||item.id::text||':'||tid::text,'Pending',0,clock_timestamp(),clock_timestamp(),clock_timestamp()
   FROM public.quotation_building_items item JOIN public.quotations q ON q.id=item.quotation_id
   WHERE q.id=quote AND q.billing_purpose='BuildingService';
  INSERT INTO public.payment_provisioning_records(id,payment_transaction_id,quotation_id,quotation_item_id,organization_id,provisioning_key,status,attempts,created_at,updated_at,next_attempt_at)
  SELECT gen_random_uuid(),tid,quote,t.id,req.organization_id,'topup:'||t.id::text||':'||tid::text,'Pending',0,clock_timestamp(),clock_timestamp(),clock_timestamp()
   FROM public.quotation_topup_items t JOIN public.quotations q ON q.id=t.quotation_id
   WHERE q.id=quote AND q.billing_purpose='AIQuotaTopUp';
  UPDATE public.billing_checkout_operations SET status='Completed',lease_token=NULL,lease_until=NULL,last_error=NULL,updated_at=clock_timestamp() WHERE id=op.id;
 END IF;
 UPDATE public.payos_webhook_inbox SET status=CASE WHEN reason IS NULL THEN 'Applied' ELSE 'Rejected' END,
  last_error=reason,processed_at=clock_timestamp(),lease_token=NULL,lease_until=NULL WHERE id=ev.id;
 INSERT INTO public.audit_logs(id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
 VALUES(gen_random_uuid(),req.organization_id,'System','Payment','payment_transactions',tid,op.id,
  jsonb_build_object('operation',CASE WHEN reason IS NULL THEN 'PaymentApplied' ELSE 'PaymentRejected' END,'reason',reason),clock_timestamp());
 RETURN tid;
END $$;

-- Provisioning records: Building lines for BuildingService, the single top-up line for AIQuotaTopUp.
-- The composite FK only covered Building lines; the trigger below validates the line of either purpose at insert,
-- provenance stays immutable, and issued lines of both tables cannot be deleted (Draft-only write triggers).
ALTER TABLE payment_provisioning_records DROP CONSTRAINT IF EXISTS fk_provisioning_quotation_line;
CREATE OR REPLACE FUNCTION validate_payment_provisioning_write() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM public.payment_transactions payment
   JOIN public.payos_payment_requests request ON request.id=payment.payment_request_id
   JOIN public.quotations quotation ON quotation.id=request.quotation_id
  WHERE payment.id=NEW.payment_transaction_id AND payment.status='Applied' AND request.quotation_id=NEW.quotation_id
   AND request.organization_id=NEW.organization_id AND quotation.organization_id=NEW.organization_id
   AND ((quotation.billing_purpose='BuildingService' AND EXISTS(SELECT 1 FROM public.quotation_building_items item
      WHERE item.id=NEW.quotation_item_id AND item.quotation_id=NEW.quotation_id AND item.building_id IS NOT NULL))
    OR (quotation.billing_purpose='AIQuotaTopUp' AND EXISTS(SELECT 1 FROM public.quotation_topup_items t
      WHERE t.id=NEW.quotation_item_id AND t.quotation_id=NEW.quotation_id AND t.organization_id=NEW.organization_id))))
 THEN RAISE EXCEPTION 'payment provisioning record requires an Applied payment and a valid line of the same organization and quotation'; END IF;
 IF TG_OP='UPDATE' AND (OLD.payment_transaction_id IS DISTINCT FROM NEW.payment_transaction_id OR OLD.quotation_id IS DISTINCT FROM NEW.quotation_id
  OR OLD.quotation_item_id IS DISTINCT FROM NEW.quotation_item_id OR OLD.organization_id IS DISTINCT FROM NEW.organization_id OR OLD.provisioning_key IS DISTINCT FROM NEW.provisioning_key)
 THEN RAISE EXCEPTION 'payment provisioning provenance is immutable'; END IF;
 RETURN NEW;
END $$;

-- 8. Provisioning context and finalisation per source.
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

CREATE OR REPLACE FUNCTION finalize_payos_provisioning(p_line uuid,p_lease uuid,p_start timestamptz,p_end timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
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
 END IF;
 UPDATE public.payment_provisioning_records SET status='Succeeded',provisioned_at=clock_timestamp(),last_error=NULL,lease_token=NULL,lease_until=NULL,updated_at=clock_timestamp() WHERE id=rec.id;
 RETURN eid;
END $$;

-- 9. Privileges: ledger owner writes through gates; API reads; browser roles get nothing.
ALTER TABLE billing_entitlement_upgrades ENABLE ROW LEVEL SECURITY;ALTER TABLE billing_upgrade_reservations ENABLE ROW LEVEL SECURITY;
ALTER TABLE quotation_topup_items ENABLE ROW LEVEL SECURITY;ALTER TABLE billing_ai_quota_allocations ENABLE ROW LEVEL SECURITY;ALTER TABLE billing_learner_seats ENABLE ROW LEVEL SECURITY;
CREATE POLICY billing_upgrade_ledger ON billing_entitlement_upgrades TO fet3d_payos_ledger_owner USING(true) WITH CHECK(true);
CREATE POLICY billing_upgrade_reservation_ledger ON billing_upgrade_reservations TO fet3d_payos_ledger_owner USING(true) WITH CHECK(true);
CREATE POLICY billing_topup_ledger ON quotation_topup_items TO fet3d_payos_ledger_owner USING(true);
CREATE POLICY billing_topup_api ON quotation_topup_items TO fire3d_api USING(true) WITH CHECK(true);
CREATE POLICY billing_allocation_ledger ON billing_ai_quota_allocations TO fet3d_payos_ledger_owner USING(true) WITH CHECK(true);
CREATE POLICY billing_upgrade_api_read ON billing_entitlement_upgrades FOR SELECT TO fire3d_api USING(true);
CREATE POLICY billing_grant_api_read ON billing_ai_quota_grants FOR SELECT TO fire3d_api USING(true);
CREATE POLICY billing_allocation_api_read ON billing_ai_quota_allocations FOR SELECT TO fire3d_api USING(true);
CREATE POLICY billing_seat_api_read ON billing_learner_seats FOR SELECT TO fire3d_api USING(true);
GRANT SELECT,INSERT ON billing_entitlement_upgrades TO fet3d_payos_ledger_owner;
-- Row locks on the upgraded entitlement need a column UPDATE privilege; no entitlement column is ever updated here.
GRANT UPDATE(id) ON service_entitlements TO fet3d_payos_ledger_owner;
GRANT SELECT,INSERT,UPDATE ON billing_upgrade_reservations,billing_ai_quota_allocations TO fet3d_payos_ledger_owner;
GRANT SELECT ON quotation_topup_items,billing_learner_seats TO fet3d_payos_ledger_owner;
GRANT SELECT,INSERT,UPDATE,DELETE ON quotation_topup_items TO fire3d_api;
GRANT SELECT ON billing_entitlement_upgrades,billing_ai_quota_grants,billing_ai_quota_allocations,billing_learner_seats TO fire3d_api;
-- Expiry reminders: notification + delivery rows are the transactional outbox; the backend worker sends and records attempts.
GRANT SELECT,INSERT,UPDATE ON organization_notifications,notification_deliveries TO fire3d_api;
CREATE INDEX IF NOT EXISTS notification_deliveries_pending ON notification_deliveries(created_at) WHERE status='Pending';
REVOKE ALL ON billing_entitlement_upgrades,billing_upgrade_reservations,billing_ai_quota_allocations,billing_learner_seats FROM PUBLIC,fet3d_payos_request_executor,fet3d_payos_webhook_executor;
REVOKE ALL ON quotation_topup_items FROM PUBLIC;
DO $roles$ DECLARE r text; BEGIN
 FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
   EXECUTE format('REVOKE ALL ON public.billing_entitlement_upgrades,public.billing_upgrade_reservations,public.quotation_topup_items,public.billing_ai_quota_allocations,public.billing_learner_seats FROM %I',r);
   EXECUTE format('REVOKE ALL ON FUNCTION public.billing_capacity_revision(uuid),public.billing_effective_learner_limit(uuid,timestamptz) FROM %I',r);
  END IF;
 END LOOP;
END $roles$;
DO $ownership$ DECLARE s record; BEGIN
 ALTER FUNCTION billing_capacity_revision(uuid) OWNER TO fet3d_payos_ledger_owner;
 ALTER FUNCTION billing_effective_learner_limit(uuid,timestamptz) OWNER TO fet3d_payos_ledger_owner;
 REVOKE ALL ON FUNCTION billing_capacity_revision(uuid),billing_effective_learner_limit(uuid,timestamptz) FROM PUBLIC;
 GRANT EXECUTE ON FUNCTION billing_capacity_revision(uuid),billing_effective_learner_limit(uuid,timestamptz) TO fire3d_api;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_ifc_upload_owner') THEN
  GRANT EXECUTE ON FUNCTION billing_effective_learner_limit(uuid,timestamptz),billing_capacity_revision(uuid) TO fet3d_ifc_upload_owner;
 END IF;
 SELECT * INTO s FROM billing_upgrade_permissions;
 IF NOT s.had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner; END IF;
 IF s.changed THEN IF s.original_set IS NULL THEN EXECUTE format('REVOKE fet3d_payos_ledger_owner FROM %I',current_user);
 ELSE EXECUTE format('GRANT fet3d_payos_ledger_owner TO %I WITH SET %s,INHERIT %s',current_user,CASE WHEN s.original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN s.original_inherit THEN 'TRUE' ELSE 'FALSE' END);END IF;END IF;
END $ownership$;

-- Pending-registration cleanup fails closed on unreadable references: expose new user/organization references to its owner.
DO $cleanup$ DECLARE t text; BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_pending_cleanup_owner') THEN
  FOREACH t IN ARRAY ARRAY['quotation_topup_items','billing_ai_quota_allocations','billing_learner_seats'] LOOP
   EXECUTE format('GRANT SELECT ON %I TO fet3d_pending_cleanup_owner',t);
   EXECUTE format('CREATE POLICY pending_cleanup_owner ON %I TO fet3d_pending_cleanup_owner USING(true) WITH CHECK(true)',t);
  END LOOP;
 END IF;
END $cleanup$;
