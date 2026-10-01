-- Forward hardening, independent of whether the baseline was created by EF or historical SQL.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname IN
    ('fet3d_payos_ledger_owner','fet3d_payos_request_executor','fet3d_payos_webhook_executor')
    AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls))
    OR pg_catalog.pg_has_role('fet3d_payos_request_executor','fet3d_payos_ledger_owner','MEMBER')
    OR pg_catalog.pg_has_role('fet3d_payos_webhook_executor','fet3d_payos_ledger_owner','MEMBER') THEN
  RAISE EXCEPTION 'PayOS roles must be dedicated NOLOGIN, unprivileged, and executors must not inherit the ledger owner';
 END IF;
END $$;
REVOKE ALL PRIVILEGES ON TABLE public.quotations FROM PUBLIC;
DO $$ DECLARE executor text; BEGIN
 FOREACH executor IN ARRAY ARRAY['fet3d_payos_request_executor','fet3d_payos_webhook_executor'] LOOP
  IF pg_catalog.has_table_privilege(executor,'public.payos_payment_requests','INSERT,UPDATE,DELETE')
     OR pg_catalog.has_table_privilege(executor,'public.payment_transactions','INSERT,UPDATE,DELETE') THEN
   RAISE EXCEPTION 'PayOS executor % inherits direct payment DML; remove unsafe memberships before migrating',executor;
  END IF;
 END LOOP;
END $$;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_catalog.pg_constraint WHERE conname='fk_billing_quotation_discount' AND conrelid='public.quotations'::regclass) THEN
  ALTER TABLE public.quotations ADD CONSTRAINT fk_billing_quotation_discount
    FOREIGN KEY(discount_rule_id) REFERENCES public.service_package_discount_rules(id) ON DELETE RESTRICT;
 END IF;
END $$;

CREATE OR REPLACE FUNCTION public.validate_billing_quotation_identity()
RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.organization_id IS DISTINCT FROM OLD.organization_id OR NEW.requested_by IS DISTINCT FROM OLD.requested_by
    OR NEW.billing_purpose IS DISTINCT FROM OLD.billing_purpose THEN
  RAISE EXCEPTION 'Quotation tenant/requester/purpose identity is immutable, including Draft';
 END IF;
 IF NEW.status IN ('Issued','Accepted') AND NEW.billing_purpose='BuildingService'
    AND EXISTS(SELECT 1 FROM public.quotation_building_items AS item
      JOIN public.buildings AS building ON building.id=item.building_id
      WHERE item.quotation_id=NEW.id AND building.organization_id<>NEW.organization_id) THEN
  RAISE EXCEPTION 'Quotation line Building organization differs from quotation tenant';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS billing_quotation_identity ON public.quotations;
CREATE TRIGGER billing_quotation_identity BEFORE UPDATE ON public.quotations
FOR EACH ROW EXECUTE FUNCTION public.validate_billing_quotation_identity();
GRANT SELECT(id,is_active,deleted_at) ON public.organizations TO fet3d_payos_ledger_owner;

CREATE OR REPLACE FUNCTION create_pending_payos_payment_request(
    p_quotation_id UUID,
    p_requested_by UUID,
    p_idempotency_key TEXT,
    p_order_code BIGINT,
    p_checkout_url TEXT,
    p_return_url TEXT,
    p_cancel_url TEXT,
    p_expires_at TIMESTAMPTZ
)
RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    v_payment_request_id UUID;
BEGIN
    IF p_quotation_id IS NULL OR p_requested_by IS NULL THEN
        RAISE EXCEPTION 'quotation_id and requested_by are required';
    END IF;
    IF NULLIF(pg_catalog.btrim(p_idempotency_key), '') IS NULL THEN
        RAISE EXCEPTION 'payment request idempotency key is required';
    END IF;
    PERFORM pg_catalog.pg_advisory_xact_lock_shared(pg_catalog.hashtextextended('fire3d:identity-management',0));
    PERFORM pg_catalog.pg_advisory_xact_lock(
        pg_catalog.hashtextextended('fet3d:payos:' || pg_catalog.btrim(p_idempotency_key), 0)
    );
    SELECT request.id INTO v_payment_request_id
    FROM public.payos_payment_requests AS request
    WHERE request.idempotency_key = pg_catalog.btrim(p_idempotency_key)
    FOR UPDATE;
    IF FOUND THEN
        IF NOT EXISTS (
            SELECT 1
            FROM public.payos_payment_requests AS request
            WHERE request.id = v_payment_request_id
              AND request.quotation_id = p_quotation_id
              AND request.requested_by = p_requested_by
              AND request.order_code = p_order_code
              AND request.checkout_url = pg_catalog.btrim(p_checkout_url)
              AND request.return_url = pg_catalog.btrim(p_return_url)
              AND request.cancel_url = pg_catalog.btrim(p_cancel_url)
              AND request.expires_at IS NOT DISTINCT FROM p_expires_at
        ) THEN
            RAISE EXCEPTION 'payment request idempotency key was reused for different input';
        END IF;
        RETURN v_payment_request_id;
    END IF;
    IF p_order_code IS NULL OR p_order_code <= 0 THEN
        RAISE EXCEPTION 'orderCode must be a positive integer';
    END IF;
    IF NULLIF(pg_catalog.btrim(p_checkout_url), '') IS NULL
       OR pg_catalog.char_length(pg_catalog.btrim(p_checkout_url)) > 2048
       OR pg_catalog.btrim(p_checkout_url) !~ '^https://[^[:space:]]+$' THEN
        RAISE EXCEPTION 'checkout_url must be a non-empty HTTPS URL up to 2048 characters';
    END IF;
    IF NULLIF(pg_catalog.btrim(p_return_url), '') IS NULL
       OR pg_catalog.char_length(pg_catalog.btrim(p_return_url)) > 2048
       OR pg_catalog.btrim(p_return_url) !~ '^https://[^[:space:]]+$' THEN
        RAISE EXCEPTION 'return_url must be a non-empty HTTPS navigation URL up to 2048 characters';
    END IF;
    IF NULLIF(pg_catalog.btrim(p_cancel_url), '') IS NULL
       OR pg_catalog.char_length(pg_catalog.btrim(p_cancel_url)) > 2048
       OR pg_catalog.btrim(p_cancel_url) !~ '^https://[^[:space:]]+$' THEN
        RAISE EXCEPTION 'cancel_url must be a non-empty HTTPS navigation URL up to 2048 characters';
    END IF;
    IF p_expires_at IS NULL OR p_expires_at <= pg_catalog.now() THEN
        RAISE EXCEPTION 'expires_at must be in the future';
    END IF;

    PERFORM 1 FROM public.quotations AS quotation
     WHERE quotation.id = p_quotation_id
     FOR UPDATE;

    INSERT INTO public.payos_payment_requests (
        quotation_id,
        organization_id,
        requested_by,
        idempotency_key,
        order_code,
        expected_amount,
        expected_currency,
        checkout_url,
        return_url,
        cancel_url,
        status,
        expires_at
    )
    SELECT quotation.id,
           quotation.organization_id,
           p_requested_by,
           pg_catalog.btrim(p_idempotency_key),
           p_order_code,
           quotation.total_amount,
           quotation.currency,
           pg_catalog.btrim(p_checkout_url),
           pg_catalog.btrim(p_return_url),
           pg_catalog.btrim(p_cancel_url),
           'Pending'::public.payment_request_status_enum,
           p_expires_at
    FROM public.quotations AS quotation
    JOIN public.users AS requester ON requester.id = p_requested_by
    JOIN public.organizations AS organization ON organization.id = quotation.organization_id
    WHERE quotation.id = p_quotation_id
      AND quotation.status = 'Accepted'
      AND quotation.valid_until > pg_catalog.now()
      AND quotation.total_amount > 0
      AND quotation.total_amount = pg_catalog.trunc(quotation.total_amount)
      AND quotation.currency = 'VND'
      AND organization.is_active AND organization.deleted_at IS NULL
      AND requester.role = 'OrganizationUser'
      AND requester.organization_id = quotation.organization_id
      AND requester.is_active
      AND requester.deleted_at IS NULL
    RETURNING id INTO v_payment_request_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'payment request requires an unexpired Accepted quotation and active OrganizationUser in the same organization';
    END IF;

    RETURN v_payment_request_id;
END;
$$;
