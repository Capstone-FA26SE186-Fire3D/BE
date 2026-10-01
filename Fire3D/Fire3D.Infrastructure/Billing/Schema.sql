-- FET3D Building billing: additive migration, derived from Docs v6.7 addd4df.
-- Never execute the complete target schema against a populated application DB.
ALTER TABLE quotations ALTER COLUMN status SET DEFAULT 'Draft'::quotation_status_enum;
ALTER TABLE payos_payment_requests ALTER COLUMN status SET DEFAULT 'Pending'::payment_request_status_enum;
ALTER TABLE payment_transactions ALTER COLUMN status SET DEFAULT 'Received'::payment_transaction_status_enum;
ALTER TABLE payment_transactions ALTER COLUMN signature_verified SET DEFAULT false;
ALTER TABLE quotations
 ADD COLUMN IF NOT EXISTS billing_purpose varchar(30) NOT NULL DEFAULT 'BuildingService',
 ADD COLUMN IF NOT EXISTS discount_rule_id uuid,
 ADD COLUMN IF NOT EXISTS discount_snapshot jsonb NOT NULL DEFAULT '{}',
 ADD COLUMN IF NOT EXISTS price_snapshot jsonb NOT NULL DEFAULT '{}',
 ADD COLUMN IF NOT EXISTS terms_snapshot jsonb NOT NULL DEFAULT '{}',
 ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='quotations' AND column_name='service_package_id') THEN
  ALTER TABLE quotations ALTER COLUMN service_package_id DROP NOT NULL;
 END IF;
END $$;
ALTER TABLE payos_payment_requests ADD COLUMN IF NOT EXISTS idempotency_key varchar(255) NOT NULL DEFAULT gen_random_uuid()::text;
CREATE UNIQUE INDEX IF NOT EXISTS ux_payos_idempotency_key ON payos_payment_requests(idempotency_key);
CREATE TABLE IF NOT EXISTS service_package_discount_rules (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    service_package_id  UUID REFERENCES service_packages(id) ON DELETE RESTRICT,
    code                VARCHAR(80) UNIQUE NOT NULL,
    discount_kind       VARCHAR(20) NOT NULL, -- Percent | Fixed
    discount_value      DECIMAL(14,2) NOT NULL,
    discount_currency   VARCHAR(3),                       -- Required for Fixed; NULL for Percent
    minimum_buildings   INT NOT NULL DEFAULT 1,
    minimum_duration_months INT,
    valid_from          TIMESTAMPTZ NOT NULL,
    valid_until         TIMESTAMPTZ,
    is_active            BOOLEAN NOT NULL DEFAULT true,
    created_by          UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_discount_kind CHECK (discount_kind IN ('Percent','Fixed')),
    CONSTRAINT check_discount_value CHECK (discount_value >= 0),
    CONSTRAINT check_discount_percent CHECK (discount_kind <> 'Percent' OR discount_value <= 100),
    CONSTRAINT check_discount_currency CHECK (
        (discount_kind = 'Fixed' AND discount_currency IS NOT NULL
            AND discount_currency ~ '^[A-Z]{3}$')
        OR (discount_kind = 'Percent' AND discount_currency IS NULL)
    ),
    CONSTRAINT check_discount_buildings CHECK (minimum_buildings > 0),
    CONSTRAINT check_discount_duration CHECK (minimum_duration_months IS NULL OR minimum_duration_months > 0),
    CONSTRAINT check_discount_dates CHECK (valid_until IS NULL OR valid_until > valid_from)
);

CREATE TABLE IF NOT EXISTS quotation_building_items (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    quotation_id          UUID REFERENCES quotations(id) ON DELETE RESTRICT NOT NULL,
    building_id           UUID REFERENCES buildings(id) ON DELETE RESTRICT NOT NULL,
    service_package_id    UUID REFERENCES service_packages(id) ON DELETE RESTRICT NOT NULL,
    purchase_action       VARCHAR(20) NOT NULL, -- New | Renewal
    service_duration_months INT NOT NULL,
    unit_price            DECIMAL(14,2) NOT NULL,
    discount_amount       DECIMAL(14,2) NOT NULL DEFAULT 0,
    subtotal_amount       DECIMAL(14,2) NOT NULL,
    total_amount          DECIMAL(14,2) NOT NULL,
    currency              VARCHAR(3) NOT NULL,
    price_snapshot        JSONB NOT NULL DEFAULT '{}',
    terms_snapshot        JSONB NOT NULL DEFAULT '{}',
    discount_snapshot     JSONB NOT NULL DEFAULT '{}',
    line_provisioning_key VARCHAR(255) UNIQUE,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (quotation_id, building_id),
    UNIQUE (id, quotation_id),
    CONSTRAINT check_quotation_item_action CHECK (purchase_action IN ('New','Renewal')),
    CONSTRAINT check_quotation_item_duration CHECK (service_duration_months > 0),
    CONSTRAINT check_quotation_item_amounts CHECK (
        unit_price >= 0 AND discount_amount >= 0 AND subtotal_amount >= 0
        AND total_amount = subtotal_amount - discount_amount
        AND total_amount >= 0 AND currency ~ '^[A-Z]{3}$'
    )
);

CREATE TABLE IF NOT EXISTS service_entitlements (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     UUID REFERENCES organizations(id) ON DELETE RESTRICT NOT NULL,
    building_id         UUID REFERENCES buildings(id) ON DELETE RESTRICT NOT NULL,
    service_package_id  UUID REFERENCES service_packages(id) ON DELETE RESTRICT NOT NULL,
    quotation_id        UUID REFERENCES quotations(id) ON DELETE RESTRICT,
    quotation_item_id   UUID,
    payment_transaction_id UUID REFERENCES payment_transactions(id) ON DELETE RESTRICT,
    provisioning_key    VARCHAR(255) NOT NULL UNIQUE,               -- Ổn định theo quotation/payment, không random mỗi retry
    status              VARCHAR(30) NOT NULL DEFAULT 'Active', -- Trial | Active | Expired | Suspended | Cancelled
    starts_at           TIMESTAMPTZ NOT NULL,
    ends_at             TIMESTAMPTZ NOT NULL,
    price_snapshot      JSONB NOT NULL DEFAULT '{}',
    terms_snapshot      JSONB NOT NULL DEFAULT '{}',
    playtest_units_granted INT NOT NULL DEFAULT 0,
    playtest_units_used INT NOT NULL DEFAULT 0,
    created_by          UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_service_entitlement_dates CHECK (ends_at > starts_at),
    CONSTRAINT check_service_entitlement_status CHECK (status IN ('Trial','Active','Expired','Suspended','Cancelled')),
    CONSTRAINT check_active_entitlement_payment_source CHECK (
        status <> 'Active' OR (
            quotation_id IS NOT NULL
            AND quotation_item_id IS NOT NULL
            AND payment_transaction_id IS NOT NULL
        )
    ),
    CONSTRAINT check_playtest_entitlement_units CHECK (
        playtest_units_granted >= 0 AND playtest_units_used >= 0 AND playtest_units_used <= playtest_units_granted
    ),
    UNIQUE (building_id, starts_at),
    UNIQUE (quotation_item_id, payment_transaction_id)
);

CREATE TABLE IF NOT EXISTS payment_provisioning_records (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_transaction_id UUID REFERENCES payment_transactions(id) ON DELETE RESTRICT NOT NULL,
    quotation_id          UUID REFERENCES quotations(id) ON DELETE RESTRICT NOT NULL,
    quotation_item_id     UUID NOT NULL,
    organization_id       UUID REFERENCES organizations(id) ON DELETE RESTRICT NOT NULL,
    provisioning_key      VARCHAR(255) UNIQUE NOT NULL,
    status                VARCHAR(30) NOT NULL DEFAULT 'Pending',
    attempts              INT NOT NULL DEFAULT 0,
    last_error            TEXT,
    provisioned_at        TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_payment_provisioning_status CHECK (status IN ('Pending','Succeeded','NeedsReconcile')),
    CONSTRAINT check_payment_provisioning_attempts CHECK (attempts >= 0),
    UNIQUE (payment_transaction_id, quotation_item_id)
);

CREATE TABLE IF NOT EXISTS organization_notifications (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id       UUID REFERENCES organizations(id) ON DELETE RESTRICT NOT NULL,
    recipient_user_id     UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL,
    building_id           UUID REFERENCES buildings(id) ON DELETE RESTRICT NOT NULL,
    entitlement_id        UUID REFERENCES service_entitlements(id) ON DELETE RESTRICT NOT NULL,
    notification_type     VARCHAR(50) NOT NULL, -- BuildingServiceExpiring
    title                 TEXT NOT NULL,
    body                  TEXT NOT NULL,
    reference_ends_at     TIMESTAMPTZ NOT NULL,
    idempotency_key       VARCHAR(255) NOT NULL UNIQUE,
    read_at               TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_org_notification_type CHECK (notification_type IN ('BuildingServiceExpiring'))
);

CREATE TABLE IF NOT EXISTS notification_deliveries (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    notification_id       UUID REFERENCES organization_notifications(id) ON DELETE RESTRICT NOT NULL,
    channel               VARCHAR(20) NOT NULL, -- Web | Email
    status                VARCHAR(20) NOT NULL DEFAULT 'Pending', -- Pending | Sent | Failed
    attempts              INT NOT NULL DEFAULT 0,
    provider_message_id   TEXT,
    last_error            TEXT,
    sent_at               TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (notification_id, channel),
    CONSTRAINT check_notification_channel CHECK (channel IN ('Web','Email')),
    CONSTRAINT check_notification_delivery_status CHECK (status IN ('Pending','Sent','Failed')),
    CONSTRAINT check_notification_attempts CHECK (attempts >= 0)
);

CREATE TABLE IF NOT EXISTS enterprise_quote_requests (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id       UUID REFERENCES organizations(id) ON DELETE RESTRICT NOT NULL,
    requested_by          UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL,
    requested_building_count INT NOT NULL,
    requested_duration_months INT,
    contact_name          TEXT NOT NULL,
    contact_email         VARCHAR(255) NOT NULL,
    contact_phone         VARCHAR(50),
    notes                 TEXT,
    status                VARCHAR(20) NOT NULL DEFAULT 'New', -- New | Contacted | Quoted | Accepted | Rejected | Cancelled
    quotation_id          UUID REFERENCES quotations(id) ON DELETE RESTRICT,
    idempotency_key       VARCHAR(255) NOT NULL UNIQUE,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_enterprise_request_count CHECK (requested_building_count > 0),
    CONSTRAINT check_enterprise_request_duration CHECK (requested_duration_months IS NULL OR requested_duration_months > 0),
    CONSTRAINT check_enterprise_request_status CHECK (status IN ('New','Contacted','Quoted','Accepted','Rejected','Cancelled'))
);

CREATE TABLE IF NOT EXISTS billing_command_receipts (
 id uuid PRIMARY KEY, actor_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
 operation varchar(80) NOT NULL, idempotency_key varchar(128) NOT NULL,
 input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[a-f0-9]{64}$'), resource_id uuid NOT NULL,
 created_at timestamptz NOT NULL, UNIQUE(actor_id,operation,idempotency_key)
);
CREATE TABLE IF NOT EXISTS billing_checkout_operations (
 id uuid PRIMARY KEY, quotation_id uuid NOT NULL REFERENCES quotations(id) ON DELETE RESTRICT,
 actor_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
 idempotency_key varchar(128) NOT NULL, input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[a-f0-9]{64}$'),
 order_code bigint NOT NULL UNIQUE CHECK(order_code>0), status varchar(30) NOT NULL,
 lease_token uuid, lease_until timestamptz, provider_result jsonb,
 created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL,
 UNIQUE(actor_id,idempotency_key), CHECK(status IN('Creating','Ready','NeedsReconcile','Failed','Expired','Cancelled')),
 CHECK((lease_token IS NULL)=(lease_until IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_billing_checkout_open_quote ON billing_checkout_operations(quotation_id)
 WHERE status IN('Creating','Ready','NeedsReconcile');
CREATE TABLE IF NOT EXISTS payos_webhook_inbox (
 id uuid PRIMARY KEY, event_key varchar(255) NOT NULL UNIQUE,
 input_hash varchar(64) NOT NULL CHECK(input_hash ~ '^[a-f0-9]{64}$'), order_code bigint NOT NULL,
 payload jsonb NOT NULL, verified_at timestamptz NOT NULL, status varchar(30) NOT NULL,
 attempts int NOT NULL CHECK(attempts>=0), lease_token uuid, lease_until timestamptz,
 next_attempt_at timestamptz NOT NULL, created_at timestamptz NOT NULL,
 CHECK(status IN('Pending','Applied','Rejected','NeedsReconcile')), CHECK((lease_token IS NULL)=(lease_until IS NULL))
);
CREATE INDEX IF NOT EXISTS idx_webhook_inbox_retry ON payos_webhook_inbox(status,next_attempt_at);
CREATE OR REPLACE FUNCTION validate_quotation_snapshot_write()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $$
DECLARE
    v_item_count INT;
    v_subtotal NUMERIC(14,2);
    v_discount NUMERIC(14,2);
    v_total NUMERIC(14,2);
BEGIN
    IF TG_OP = 'INSERT' AND NEW.status <> 'Draft' THEN
        RAISE EXCEPTION 'new quotations must start in Draft';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status IS DISTINCT FROM NEW.status AND NOT (
        (OLD.status = 'Draft' AND NEW.status IN ('Issued','Cancelled'))
        OR (OLD.status = 'Issued' AND NEW.status IN ('Accepted','Expired','Cancelled'))
        OR (OLD.status = 'Accepted' AND NEW.status IN ('Expired','Cancelled'))
    ) THEN
        RAISE EXCEPTION 'quotation status transition is not allowed';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.id IS DISTINCT FROM NEW.id THEN
        RAISE EXCEPTION 'quotation identity is immutable';
    END IF;

    -- Acceptance records the transition timestamp exactly once. The backend
    -- may supply it, but the database fills it when the transition omits it.
    IF TG_OP = 'UPDATE' AND OLD.status = 'Issued' AND NEW.status = 'Accepted' THEN
        IF OLD.accepted_at IS NOT NULL THEN
            RAISE EXCEPTION 'quotation has already been accepted';
        END IF;
        NEW.accepted_at := COALESCE(NEW.accepted_at, pg_catalog.clock_timestamp());
    END IF;

    IF TG_OP = 'UPDATE' AND OLD.accepted_at IS NOT NULL
       AND NEW.accepted_at IS DISTINCT FROM OLD.accepted_at THEN
        RAISE EXCEPTION 'accepted_at is immutable after it is first recorded';
    END IF;

    IF TG_OP = 'UPDATE' AND OLD.status <> 'Draft' AND (
        OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.billing_purpose IS DISTINCT FROM NEW.billing_purpose
        OR OLD.quantity IS DISTINCT FROM NEW.quantity
        OR OLD.unit_price IS DISTINCT FROM NEW.unit_price
        OR OLD.subtotal_amount IS DISTINCT FROM NEW.subtotal_amount
        OR OLD.tax_amount IS DISTINCT FROM NEW.tax_amount
        OR OLD.discount_amount IS DISTINCT FROM NEW.discount_amount
        OR OLD.total_amount IS DISTINCT FROM NEW.total_amount
        OR OLD.currency IS DISTINCT FROM NEW.currency
        OR OLD.price_snapshot IS DISTINCT FROM NEW.price_snapshot
        OR OLD.terms_snapshot IS DISTINCT FROM NEW.terms_snapshot
        OR OLD.discount_rule_id IS DISTINCT FROM NEW.discount_rule_id
        OR OLD.discount_snapshot IS DISTINCT FROM NEW.discount_snapshot
        OR OLD.requested_by IS DISTINCT FROM NEW.requested_by
        OR OLD.quotation_number IS DISTINCT FROM NEW.quotation_number
        OR OLD.valid_until IS DISTINCT FROM NEW.valid_until
        OR OLD.issued_by IS DISTINCT FROM NEW.issued_by
        OR OLD.issued_at IS DISTINCT FROM NEW.issued_at
    ) THEN
        RAISE EXCEPTION 'issued or accepted quotation snapshot is immutable';
    END IF;

    IF NEW.billing_purpose = 'BuildingService' AND NEW.status IN ('Issued','Accepted') THEN
        SELECT count(*), COALESCE(sum(item.subtotal_amount),0),
               COALESCE(sum(item.discount_amount),0), COALESCE(sum(item.total_amount),0)
          INTO v_item_count, v_subtotal, v_discount, v_total
          FROM public.quotation_building_items AS item
         WHERE item.quotation_id = NEW.id;
        IF v_item_count = 0 THEN
            RAISE EXCEPTION 'BuildingService quotation must contain at least one Building line';
        END IF;
        IF EXISTS (
            SELECT 1 FROM public.quotation_building_items AS item
             WHERE item.quotation_id = NEW.id
               AND NULLIF(pg_catalog.btrim(item.line_provisioning_key),'') IS NULL
        ) THEN
            RAISE EXCEPTION 'Issued BuildingService quotation lines require provisioning keys';
        END IF;
        IF NEW.quantity <> v_item_count
           OR NEW.subtotal_amount <> v_subtotal
           OR NEW.discount_amount <> v_discount
           OR NEW.total_amount <> v_total + NEW.tax_amount THEN
            RAISE EXCEPTION 'quotation totals must equal its Building line snapshots';
        END IF;
    ELSIF NEW.billing_purpose = 'AIUsage' AND EXISTS (
        SELECT 1 FROM public.quotation_building_items AS item WHERE item.quotation_id = NEW.id
    ) THEN
        RAISE EXCEPTION 'AIUsage quotation cannot contain Building service lines';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION validate_quotation_building_item_write()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $$
DECLARE
    v_quotation public.quotations%ROWTYPE;
    v_building public.buildings%ROWTYPE;
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.quotation_id IS DISTINCT FROM NEW.quotation_id THEN
        RAISE EXCEPTION 'quotation line cannot move between quotations';
    END IF;

    SELECT * INTO v_quotation FROM public.quotations
     WHERE id = CASE
                    WHEN TG_OP = 'DELETE' THEN OLD.quotation_id
                    ELSE NEW.quotation_id
                END
     FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'quotation does not exist'; END IF;
    IF v_quotation.billing_purpose <> 'BuildingService' THEN
        RAISE EXCEPTION 'quotation line requires a BuildingService quotation';
    END IF;
    IF v_quotation.status <> 'Draft' THEN
        RAISE EXCEPTION 'quotation lines can only change while quotation is Draft';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    SELECT * INTO v_building FROM public.buildings WHERE id = NEW.building_id FOR KEY SHARE;
    IF NOT FOUND OR v_building.organization_id <> v_quotation.organization_id
       OR NULLIF(pg_catalog.btrim(v_building.name),'') IS NULL
       OR NOT EXISTS(SELECT 1 FROM public.building_locations loc WHERE loc.building_id=NEW.building_id AND NULLIF(pg_catalog.btrim(loc.address),'') IS NOT NULL) THEN
        RAISE EXCEPTION 'quotation line requires a named Building with address in the same organization';
    END IF;
    IF TG_OP = 'UPDATE' AND v_quotation.status <> 'Draft' AND (
        OLD.building_id IS DISTINCT FROM NEW.building_id
        OR OLD.service_package_id IS DISTINCT FROM NEW.service_package_id
    ) THEN
        RAISE EXCEPTION 'quotation line identity and commercial provenance are immutable';
    END IF;
    IF NEW.currency <> v_quotation.currency THEN
        RAISE EXCEPTION 'quotation line currency must match quotation currency';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION validate_service_entitlement_write()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF (TG_OP = 'INSERT' OR (TG_OP = 'UPDATE' AND OLD.status IS DISTINCT FROM NEW.status AND NEW.status = 'Active'))
       AND NOT EXISTS (
        SELECT 1
        FROM public.buildings AS building
        WHERE building.id = NEW.building_id
          AND building.organization_id = NEW.organization_id
    ) THEN
        RAISE EXCEPTION 'service entitlement building and organization must match';
    END IF;

    IF NEW.quotation_item_id IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM public.quotation_building_items AS item
        JOIN public.quotations AS quotation ON quotation.id = item.quotation_id
        WHERE item.id = NEW.quotation_item_id
          AND item.quotation_id = NEW.quotation_id
          AND quotation.billing_purpose = 'BuildingService'
          AND quotation.organization_id = NEW.organization_id
          AND item.building_id = NEW.building_id
          AND item.service_package_id = NEW.service_package_id
          AND item.price_snapshot = NEW.price_snapshot
          AND item.terms_snapshot = NEW.terms_snapshot
    ) THEN
        RAISE EXCEPTION 'service entitlement quotation item must match Building, organization and snapshots';
    END IF;

    IF NEW.payment_transaction_id IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM public.payment_transactions AS payment
        JOIN public.payos_payment_requests AS request
          ON request.id = payment.payment_request_id
        WHERE payment.id = NEW.payment_transaction_id
          AND payment.status = 'Applied'
          AND request.quotation_id = NEW.quotation_id
          AND request.organization_id = NEW.organization_id
    ) THEN
        RAISE EXCEPTION 'service entitlement payment must be the Applied transaction for its quotation';
    END IF;

    IF NEW.status = 'Active' AND (NEW.quotation_item_id IS NULL OR NEW.payment_transaction_id IS NULL
        OR NEW.provisioning_key IS DISTINCT FROM (
        'service:' || NEW.quotation_item_id::TEXT || ':' || NEW.payment_transaction_id::TEXT
    )) THEN
        RAISE EXCEPTION 'Active service entitlement requires a deterministic quotation-line provisioning key';
    END IF;

    IF TG_OP = 'UPDATE' AND (
        OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.building_id IS DISTINCT FROM NEW.building_id
        OR OLD.service_package_id IS DISTINCT FROM NEW.service_package_id
        OR OLD.quotation_id IS DISTINCT FROM NEW.quotation_id
        OR OLD.quotation_item_id IS DISTINCT FROM NEW.quotation_item_id
        OR OLD.payment_transaction_id IS DISTINCT FROM NEW.payment_transaction_id
        OR OLD.provisioning_key IS DISTINCT FROM NEW.provisioning_key
        OR OLD.starts_at IS DISTINCT FROM NEW.starts_at
        OR OLD.ends_at IS DISTINCT FROM NEW.ends_at
        OR OLD.price_snapshot IS DISTINCT FROM NEW.price_snapshot
        OR OLD.terms_snapshot IS DISTINCT FROM NEW.terms_snapshot
        OR OLD.created_by IS DISTINCT FROM NEW.created_by
    ) THEN
        RAISE EXCEPTION 'service entitlement provenance and identity are immutable';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION validate_payment_provisioning_write()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM public.payment_transactions AS payment
        JOIN public.payos_payment_requests AS request
          ON request.id = payment.payment_request_id
        JOIN public.quotations AS quotation
          ON quotation.id = request.quotation_id
        JOIN public.quotation_building_items AS item
          ON item.id = NEW.quotation_item_id
         AND item.quotation_id = NEW.quotation_id
        WHERE payment.id = NEW.payment_transaction_id
          AND payment.status = 'Applied'
          AND request.quotation_id = NEW.quotation_id
          AND request.organization_id = NEW.organization_id
           AND quotation.billing_purpose = 'BuildingService'
           AND quotation.organization_id = NEW.organization_id
           AND item.building_id IS NOT NULL
    ) THEN
        RAISE EXCEPTION 'payment provisioning record requires an Applied BuildingService payment and valid quotation line for the same organization and quotation';
    END IF;
    IF TG_OP = 'UPDATE' AND (
        OLD.payment_transaction_id IS DISTINCT FROM NEW.payment_transaction_id
        OR OLD.quotation_id IS DISTINCT FROM NEW.quotation_id
        OR OLD.quotation_item_id IS DISTINCT FROM NEW.quotation_item_id
        OR OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.provisioning_key IS DISTINCT FROM NEW.provisioning_key
    ) THEN
        RAISE EXCEPTION 'payment provisioning provenance is immutable';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION validate_organization_notification_write()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (
        OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.recipient_user_id IS DISTINCT FROM NEW.recipient_user_id
        OR OLD.building_id IS DISTINCT FROM NEW.building_id
        OR OLD.entitlement_id IS DISTINCT FROM NEW.entitlement_id
        OR OLD.notification_type IS DISTINCT FROM NEW.notification_type
        OR OLD.reference_ends_at IS DISTINCT FROM NEW.reference_ends_at
    ) THEN
        RAISE EXCEPTION 'notification identity and tenant scope are immutable';
    END IF;
    IF NEW.notification_type <> 'BuildingServiceExpiring'
       OR NOT EXISTS (
            SELECT 1
              FROM public.users AS recipient
             WHERE recipient.id = NEW.recipient_user_id
               AND recipient.organization_id = NEW.organization_id
               AND recipient.role = 'OrganizationUser'
       )
       OR NOT EXISTS (
            SELECT 1
              FROM public.buildings AS building
             WHERE building.id = NEW.building_id
               AND building.organization_id = NEW.organization_id
       )
       OR NOT EXISTS (
            SELECT 1
              FROM public.service_entitlements AS entitlement
             WHERE entitlement.id = NEW.entitlement_id
               AND entitlement.organization_id = NEW.organization_id
               AND entitlement.building_id = NEW.building_id
               AND entitlement.ends_at = NEW.reference_ends_at
       ) THEN
        RAISE EXCEPTION 'expiry notification recipient, Building and entitlement must share the organization and expiry';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION enforce_payment_transaction_state()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Payment transaction provenance is append-only';
    END IF;

    IF CURRENT_USER != 'fet3d_payos_ledger_owner' THEN
        RAISE EXCEPTION 'Payment transaction writes must use trusted PayOS webhook function';
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.status != 'Received'
           OR NEW.signature_verified
           OR NEW.signature_verified_at IS NOT NULL
           OR NEW.processed_at IS NOT NULL
           OR NEW.rejection_reason IS NOT NULL THEN
            RAISE EXCEPTION 'Payment transaction must be inserted as unverified Received';
        END IF;
        RETURN NEW;
    END IF;

    IF OLD.status IN ('Applied', 'Rejected') THEN
        IF NEW IS DISTINCT FROM OLD THEN
            RAISE EXCEPTION 'Applied or Rejected payment provenance is immutable';
        END IF;
        RETURN NEW;
    END IF;

    IF OLD.id IS DISTINCT FROM NEW.id
       OR OLD.payment_request_id IS DISTINCT FROM NEW.payment_request_id
       OR OLD.webhook_event_id IS DISTINCT FROM NEW.webhook_event_id
       OR OLD.provider_transaction_id IS DISTINCT FROM NEW.provider_transaction_id
       OR OLD.received_order_code IS DISTINCT FROM NEW.received_order_code
       OR OLD.received_amount IS DISTINCT FROM NEW.received_amount
       OR OLD.received_currency IS DISTINCT FROM NEW.received_currency
       OR OLD.raw_payload IS DISTINCT FROM NEW.raw_payload
       OR OLD.received_at IS DISTINCT FROM NEW.received_at THEN
        RAISE EXCEPTION 'Payment webhook identity and received payload are immutable';
    END IF;

    IF OLD.status = 'Received' AND NEW.status = 'Verified' THEN
        RETURN NEW;
    END IF;

    IF OLD.status = 'Verified' AND NEW.status IN ('Applied', 'Rejected') THEN
        IF NOT NEW.signature_verified
           OR NEW.signature_verified_at IS DISTINCT FROM OLD.signature_verified_at THEN
            RAISE EXCEPTION 'Verified signature provenance cannot be removed or replaced';
        END IF;
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'Invalid payment transaction transition: % -> %', OLD.status, NEW.status;
END;
$$ LANGUAGE plpgsql;

-- Paid chỉ được ghi khi transaction của chính request đã Applied sau verified webhook
-- và ba giá trị orderCode, amount, currency khớp hoàn toàn với request mong đợi.

-- Runtime payment creation path. The backend calls PayOS outside any DB transaction,
-- then invokes this short function with the returned HTTPS checkout URL. Amount,
-- currency and organization are derived from the locked Accepted quotation; callers
-- cannot choose them or create a status other than Pending.
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
    WHERE quotation.id = p_quotation_id
      AND quotation.status = 'Accepted'
      AND quotation.valid_until > pg_catalog.now()
      AND quotation.total_amount > 0
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

CREATE OR REPLACE FUNCTION validate_payos_paid_request()
RETURNS TRIGGER AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM public.quotations AS quotation
         WHERE quotation.id = NEW.quotation_id
           AND quotation.organization_id = NEW.organization_id
           AND quotation.total_amount = NEW.expected_amount
           AND quotation.currency = NEW.expected_currency
    ) THEN RAISE EXCEPTION 'PayOS request must match quotation organization, amount and currency'; END IF;

    IF TG_OP = 'INSERT' AND NOT EXISTS (
        SELECT 1 FROM public.users AS requester
         WHERE requester.id = NEW.requested_by AND requester.role = 'OrganizationUser'
           AND requester.organization_id = NEW.organization_id
           AND requester.is_active AND requester.deleted_at IS NULL
    ) THEN RAISE EXCEPTION 'checkout requester must be an active OrganizationUser in the same tenant'; END IF;

    IF TG_OP = 'UPDATE' AND (
        OLD.quotation_id IS DISTINCT FROM NEW.quotation_id
        OR OLD.organization_id IS DISTINCT FROM NEW.organization_id
        OR OLD.requested_by IS DISTINCT FROM NEW.requested_by
        OR OLD.idempotency_key IS DISTINCT FROM NEW.idempotency_key
        OR OLD.order_code IS DISTINCT FROM NEW.order_code
        OR OLD.expected_amount IS DISTINCT FROM NEW.expected_amount
        OR OLD.expected_currency IS DISTINCT FROM NEW.expected_currency
    ) THEN RAISE EXCEPTION 'PayOS request identity and quotation snapshot are immutable'; END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'Paid' AND NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'Paid payment truth and provenance are immutable';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status = 'Paid' AND OLD.status <> 'Pending' THEN
        RAISE EXCEPTION 'Only a Pending payment request can become Paid';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status = 'Paid' AND CURRENT_USER <> 'fet3d_payos_ledger_owner' THEN
        RAISE EXCEPTION 'Paid payment request must use the trusted PayOS webhook function';
    END IF;
    IF NEW.status = 'Paid' AND NOT EXISTS (
        SELECT 1 FROM public.payment_transactions AS payment
         WHERE payment.id = NEW.paid_transaction_id AND payment.payment_request_id = NEW.id
           AND payment.status = 'Applied' AND payment.signature_verified
           AND payment.received_order_code = NEW.order_code
           AND payment.received_amount = NEW.expected_amount
           AND payment.received_currency = NEW.expected_currency
    ) THEN RAISE EXCEPTION 'Paid requires an Applied verified webhook matching the request snapshot'; END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'fet3d_backend_executor') THEN
        CREATE ROLE fet3d_backend_executor NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOREPLICATION NOBYPASSRLS;
    END IF;
END;
$$;

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
    WHERE quotation.id = p_quotation_id
      AND quotation.status = 'Accepted'
      AND quotation.valid_until > pg_catalog.now()
      AND quotation.total_amount > 0
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

CREATE OR REPLACE FUNCTION apply_verified_payos_webhook(
    p_payment_request_id UUID,
    p_webhook_event_id TEXT,
    p_provider_transaction_id TEXT,
    p_received_order_code BIGINT,
    p_received_amount NUMERIC,
    p_received_currency TEXT,
    p_raw_payload JSONB
)
RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $$
DECLARE
    v_existing       public.payment_transactions%ROWTYPE;
    v_request        public.payos_payment_requests%ROWTYPE;
    v_transaction    public.payment_transactions%ROWTYPE;
    v_verified_at    TIMESTAMPTZ;
    v_processed_at   TIMESTAMPTZ;
    v_rejection      TEXT;
BEGIN
    IF p_payment_request_id IS NULL THEN
        RAISE EXCEPTION 'payment_request_id is required';
    END IF;
    IF NULLIF(pg_catalog.btrim(p_webhook_event_id), '') IS NULL THEN
        RAISE EXCEPTION 'webhook_event_id is required';
    END IF;
    IF pg_catalog.char_length(pg_catalog.btrim(p_webhook_event_id)) > 255 THEN
        RAISE EXCEPTION 'webhook_event_id is too long';
    END IF;
    IF p_provider_transaction_id IS NOT NULL AND (
        NULLIF(pg_catalog.btrim(p_provider_transaction_id), '') IS NULL
        OR pg_catalog.char_length(pg_catalog.btrim(p_provider_transaction_id)) > 255
    ) THEN
        RAISE EXCEPTION 'provider_transaction_id must be non-empty and at most 255 characters when supplied';
    END IF;
    IF p_received_order_code IS NULL
       OR p_received_amount IS NULL
       OR p_received_currency IS NULL
       OR p_raw_payload IS NULL THEN
        RAISE EXCEPTION 'orderCode, amount, currency and raw payload are required';
    END IF;
    IF p_received_order_code <= 0 OR p_received_amount <= 0 THEN
        RAISE EXCEPTION 'orderCode and amount must be positive';
    END IF;
    IF p_received_currency !~ '^[A-Z]{3}$' THEN
        RAISE EXCEPTION 'currency must be a three-letter uppercase code';
    END IF;
    IF pg_catalog.jsonb_typeof(p_raw_payload) != 'object' THEN
        RAISE EXCEPTION 'raw payload must be a JSON object';
    END IF;

    -- Serialize identical webhook events before checking/inserting the idempotency key.
    PERFORM pg_catalog.pg_advisory_xact_lock(
        pg_catalog.hashtextextended(p_webhook_event_id, 0)
    );

    SELECT transaction.*
    INTO v_existing
    FROM public.payment_transactions AS transaction
    WHERE transaction.webhook_event_id = p_webhook_event_id;

    IF FOUND THEN
        IF v_existing.payment_request_id IS DISTINCT FROM p_payment_request_id
           OR v_existing.provider_transaction_id IS DISTINCT FROM p_provider_transaction_id
           OR v_existing.received_order_code IS DISTINCT FROM p_received_order_code
           OR v_existing.received_amount IS DISTINCT FROM p_received_amount
           OR v_existing.received_currency IS DISTINCT FROM p_received_currency
           OR v_existing.raw_payload IS DISTINCT FROM p_raw_payload THEN
            RAISE EXCEPTION 'webhook_event_id was already used with different payment data';
        END IF;

        IF v_existing.status NOT IN ('Applied', 'Rejected') THEN
            RAISE EXCEPTION 'webhook_event_id exists in a non-terminal state; privileged intervention required';
        END IF;

        RETURN v_existing.id;
    END IF;

    SELECT request.*
    INTO v_request
    FROM public.payos_payment_requests AS request
    WHERE request.id = p_payment_request_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'payment request % does not exist', p_payment_request_id;
    END IF;
    IF v_request.status != 'Pending' THEN
        RAISE EXCEPTION 'payment request % is not Pending', p_payment_request_id;
    END IF;

    INSERT INTO public.payment_transactions (
        payment_request_id,
        webhook_event_id,
        provider_transaction_id,
        received_order_code,
        received_amount,
        received_currency,
        raw_payload
    )
    VALUES (
        p_payment_request_id,
        p_webhook_event_id,
        p_provider_transaction_id,
        p_received_order_code,
        p_received_amount,
        p_received_currency,
        p_raw_payload
    )
    RETURNING * INTO v_transaction;

    v_verified_at := pg_catalog.clock_timestamp();
    UPDATE public.payment_transactions
    SET status = 'Verified',
        signature_verified = true,
        signature_verified_at = v_verified_at
    WHERE id = v_transaction.id
    RETURNING * INTO v_transaction;

    v_processed_at := pg_catalog.clock_timestamp();
    IF v_transaction.received_order_code IS DISTINCT FROM v_request.order_code
       OR v_transaction.received_amount IS DISTINCT FROM v_request.expected_amount
       OR v_transaction.received_currency IS DISTINCT FROM v_request.expected_currency THEN
        v_rejection := pg_catalog.format(
            'Verified PayOS webhook does not match expected orderCode, amount or currency'
        );
        UPDATE public.payment_transactions
        SET status = 'Rejected',
            rejection_reason = v_rejection,
            processed_at = v_processed_at
        WHERE id = v_transaction.id;
        RETURN v_transaction.id;
    END IF;

    UPDATE public.payment_transactions
    SET status = 'Applied',
        processed_at = v_processed_at
    WHERE id = v_transaction.id;

    UPDATE public.payos_payment_requests
    SET status = 'Paid',
        paid_transaction_id = v_transaction.id,
        paid_at = v_processed_at
    WHERE id = v_request.id;

    RETURN v_transaction.id;
END;
$$;

DROP TRIGGER IF EXISTS billing_quotations ON quotations;
CREATE TRIGGER billing_quotations BEFORE INSERT OR UPDATE ON quotations FOR EACH ROW EXECUTE FUNCTION validate_quotation_snapshot_write();

DROP TRIGGER IF EXISTS billing_quotation_building_items ON quotation_building_items;
CREATE TRIGGER billing_quotation_building_items BEFORE INSERT OR UPDATE OR DELETE ON quotation_building_items FOR EACH ROW EXECUTE FUNCTION validate_quotation_building_item_write();

DROP TRIGGER IF EXISTS billing_service_entitlements ON service_entitlements;
CREATE TRIGGER billing_service_entitlements BEFORE INSERT OR UPDATE ON service_entitlements FOR EACH ROW EXECUTE FUNCTION validate_service_entitlement_write();

DROP TRIGGER IF EXISTS billing_payment_provisioning_records ON payment_provisioning_records;
CREATE TRIGGER billing_payment_provisioning_records BEFORE INSERT OR UPDATE ON payment_provisioning_records FOR EACH ROW EXECUTE FUNCTION validate_payment_provisioning_write();

DROP TRIGGER IF EXISTS billing_organization_notifications ON organization_notifications;
CREATE TRIGGER billing_organization_notifications BEFORE INSERT OR UPDATE ON organization_notifications FOR EACH ROW EXECUTE FUNCTION validate_organization_notification_write();

DROP TRIGGER IF EXISTS billing_payment_transactions ON payment_transactions;
CREATE TRIGGER billing_payment_transactions BEFORE INSERT OR UPDATE OR DELETE ON payment_transactions FOR EACH ROW EXECUTE FUNCTION enforce_payment_transaction_state();

DROP TRIGGER IF EXISTS billing_payos_payment_requests ON payos_payment_requests;
CREATE TRIGGER billing_payos_payment_requests BEFORE INSERT OR UPDATE ON payos_payment_requests FOR EACH ROW EXECUTE FUNCTION validate_payos_paid_request();

DO $roles$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'fet3d_payos_ledger_owner'
    ) THEN
        CREATE ROLE fet3d_payos_ledger_owner
            NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'fet3d_payos_webhook_executor'
    ) THEN
        CREATE ROLE fet3d_payos_webhook_executor
            NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOREPLICATION NOBYPASSRLS;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'fet3d_payos_request_executor'
    ) THEN
        CREATE ROLE fet3d_payos_request_executor
            NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOREPLICATION NOBYPASSRLS;
    END IF;
END;
$roles$;

ALTER TABLE payos_payment_requests OWNER TO fet3d_payos_ledger_owner;
ALTER TABLE payment_transactions OWNER TO fet3d_payos_ledger_owner;
ALTER FUNCTION create_pending_payos_payment_request(
    UUID, UUID, TEXT, BIGINT, TEXT, TEXT, TEXT, TIMESTAMPTZ
) OWNER TO fet3d_payos_ledger_owner;
ALTER FUNCTION apply_verified_payos_webhook(UUID, TEXT, TEXT, BIGINT, NUMERIC, TEXT, JSONB)
    OWNER TO fet3d_payos_ledger_owner;

REVOKE ALL PRIVILEGES ON TABLE payos_payment_requests FROM PUBLIC;
REVOKE ALL PRIVILEGES ON TABLE payment_transactions FROM PUBLIC;
REVOKE ALL PRIVILEGES ON TABLE payos_payment_requests FROM fet3d_payos_request_executor;
REVOKE ALL PRIVILEGES ON TABLE payment_transactions FROM fet3d_payos_request_executor;
REVOKE ALL PRIVILEGES ON TABLE payos_payment_requests FROM fet3d_payos_webhook_executor;
REVOKE ALL PRIVILEGES ON TABLE payment_transactions FROM fet3d_payos_webhook_executor;
REVOKE ALL PRIVILEGES ON FUNCTION create_pending_payos_payment_request(
    UUID, UUID, TEXT, BIGINT, TEXT, TEXT, TEXT, TIMESTAMPTZ
) FROM PUBLIC;
REVOKE ALL PRIVILEGES ON FUNCTION apply_verified_payos_webhook(
    UUID, TEXT, TEXT, BIGINT, NUMERIC, TEXT, JSONB
) FROM PUBLIC;

GRANT USAGE ON SCHEMA public
TO fet3d_payos_request_executor, fet3d_payos_webhook_executor, fet3d_payos_ledger_owner;
GRANT SELECT (id, organization_id, status, total_amount, currency, valid_until)
ON TABLE quotations TO fet3d_payos_ledger_owner;
-- PostgreSQL requires UPDATE privilege for SELECT ... FOR UPDATE. The ledger
-- owner is NOLOGIN and receives only this minimal column privilege; the
-- request/webhook executors do not inherit it or receive quotation DML.
GRANT UPDATE (id) ON TABLE quotations TO fet3d_payos_ledger_owner;
GRANT SELECT (id, organization_id, role, is_active, deleted_at)
ON TABLE users TO fet3d_payos_ledger_owner;
GRANT EXECUTE ON FUNCTION create_pending_payos_payment_request(
    UUID, UUID, TEXT, BIGINT, TEXT, TEXT, TEXT, TIMESTAMPTZ
) TO fet3d_payos_request_executor;
GRANT EXECUTE ON FUNCTION apply_verified_payos_webhook(
    UUID, TEXT, TEXT, BIGINT, NUMERIC, TEXT, JSONB
) TO fet3d_payos_webhook_executor;





































CREATE UNIQUE INDEX IF NOT EXISTS ux_billing_line_identity_quote ON quotation_building_items(id,quotation_id);
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='fk_entitlement_quotation_line') THEN
  ALTER TABLE service_entitlements ADD CONSTRAINT fk_entitlement_quotation_line FOREIGN KEY(quotation_item_id,quotation_id) REFERENCES quotation_building_items(id,quotation_id) ON DELETE RESTRICT;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='fk_provisioning_quotation_line') THEN
  ALTER TABLE payment_provisioning_records ADD CONSTRAINT fk_provisioning_quotation_line FOREIGN KEY(quotation_item_id,quotation_id) REFERENCES quotation_building_items(id,quotation_id) ON DELETE RESTRICT;
 END IF;
END $$;
REVOKE ALL ON billing_command_receipts,billing_checkout_operations,payos_webhook_inbox,
 service_entitlements,payment_provisioning_records,quotation_building_items,enterprise_quote_requests,
 service_package_discount_rules,organization_notifications,notification_deliveries FROM PUBLIC;
DO $$ DECLARE role_name text; BEGIN
 FOREACH role_name IN ARRAY ARRAY['anon','authenticated'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=role_name) THEN
   EXECUTE format('REVOKE ALL ON public.quotations,public.payos_payment_requests,public.payment_transactions,public.billing_command_receipts,public.billing_checkout_operations,public.payos_webhook_inbox,public.service_entitlements,public.payment_provisioning_records,public.quotation_building_items,public.enterprise_quote_requests,public.service_package_discount_rules,public.organization_notifications,public.notification_deliveries FROM %I',role_name);
  END IF;
 END LOOP;
END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_discount_kind' AND conrelid='public.service_package_discount_rules'::regclass) THEN ALTER TABLE public.service_package_discount_rules ADD CONSTRAINT check_discount_kind CHECK (discount_kind IN ('Percent','Fixed')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_discount_value' AND conrelid='public.service_package_discount_rules'::regclass) THEN ALTER TABLE public.service_package_discount_rules ADD CONSTRAINT check_discount_value CHECK (discount_value >= 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_discount_percent' AND conrelid='public.service_package_discount_rules'::regclass) THEN ALTER TABLE public.service_package_discount_rules ADD CONSTRAINT check_discount_percent CHECK (discount_kind <> 'Percent' OR discount_value <= 100); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_discount_currency' AND conrelid='public.service_package_discount_rules'::regclass) THEN ALTER TABLE public.service_package_discount_rules ADD CONSTRAINT check_discount_currency CHECK (
        (discount_kind = 'Fixed' AND discount_currency IS NOT NULL
            AND discount_currency ~ '^[A-Z]{3}$')
        OR (discount_kind = 'Percent' AND discount_currency IS NULL)
    ); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_discount_buildings' AND conrelid='public.service_package_discount_rules'::regclass) THEN ALTER TABLE public.service_package_discount_rules ADD CONSTRAINT check_discount_buildings CHECK (minimum_buildings > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_discount_duration' AND conrelid='public.service_package_discount_rules'::regclass) THEN ALTER TABLE public.service_package_discount_rules ADD CONSTRAINT check_discount_duration CHECK (minimum_duration_months IS NULL OR minimum_duration_months > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_discount_dates' AND conrelid='public.service_package_discount_rules'::regclass) THEN ALTER TABLE public.service_package_discount_rules ADD CONSTRAINT check_discount_dates CHECK (valid_until IS NULL OR valid_until > valid_from); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_item_action' AND conrelid='public.quotation_building_items'::regclass) THEN ALTER TABLE public.quotation_building_items ADD CONSTRAINT check_quotation_item_action CHECK (purchase_action IN ('New','Renewal')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_item_duration' AND conrelid='public.quotation_building_items'::regclass) THEN ALTER TABLE public.quotation_building_items ADD CONSTRAINT check_quotation_item_duration CHECK (service_duration_months > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_item_amounts' AND conrelid='public.quotation_building_items'::regclass) THEN ALTER TABLE public.quotation_building_items ADD CONSTRAINT check_quotation_item_amounts CHECK (
        unit_price >= 0 AND discount_amount >= 0 AND subtotal_amount >= 0
        AND total_amount = subtotal_amount - discount_amount
        AND total_amount >= 0 AND currency ~ '^[A-Z]{3}$'
    ); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_service_entitlement_dates' AND conrelid='public.service_entitlements'::regclass) THEN ALTER TABLE public.service_entitlements ADD CONSTRAINT check_service_entitlement_dates CHECK (ends_at > starts_at); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_service_entitlement_status' AND conrelid='public.service_entitlements'::regclass) THEN ALTER TABLE public.service_entitlements ADD CONSTRAINT check_service_entitlement_status CHECK (status IN ('Trial','Active','Expired','Suspended','Cancelled')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_active_entitlement_payment_source' AND conrelid='public.service_entitlements'::regclass) THEN ALTER TABLE public.service_entitlements ADD CONSTRAINT check_active_entitlement_payment_source CHECK (
        status <> 'Active' OR (
            quotation_id IS NOT NULL
            AND quotation_item_id IS NOT NULL
            AND payment_transaction_id IS NOT NULL
        )
    ); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_playtest_entitlement_units' AND conrelid='public.service_entitlements'::regclass) THEN ALTER TABLE public.service_entitlements ADD CONSTRAINT check_playtest_entitlement_units CHECK (
        playtest_units_granted >= 0 AND playtest_units_used >= 0 AND playtest_units_used <= playtest_units_granted
    ); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payment_provisioning_status' AND conrelid='public.payment_provisioning_records'::regclass) THEN ALTER TABLE public.payment_provisioning_records ADD CONSTRAINT check_payment_provisioning_status CHECK (status IN ('Pending','Succeeded','NeedsReconcile')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payment_provisioning_attempts' AND conrelid='public.payment_provisioning_records'::regclass) THEN ALTER TABLE public.payment_provisioning_records ADD CONSTRAINT check_payment_provisioning_attempts CHECK (attempts >= 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_org_notification_type' AND conrelid='public.organization_notifications'::regclass) THEN ALTER TABLE public.organization_notifications ADD CONSTRAINT check_org_notification_type CHECK (notification_type IN ('BuildingServiceExpiring')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_notification_channel' AND conrelid='public.notification_deliveries'::regclass) THEN ALTER TABLE public.notification_deliveries ADD CONSTRAINT check_notification_channel CHECK (channel IN ('Web','Email')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_notification_delivery_status' AND conrelid='public.notification_deliveries'::regclass) THEN ALTER TABLE public.notification_deliveries ADD CONSTRAINT check_notification_delivery_status CHECK (status IN ('Pending','Sent','Failed')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_notification_attempts' AND conrelid='public.notification_deliveries'::regclass) THEN ALTER TABLE public.notification_deliveries ADD CONSTRAINT check_notification_attempts CHECK (attempts >= 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_enterprise_request_count' AND conrelid='public.enterprise_quote_requests'::regclass) THEN ALTER TABLE public.enterprise_quote_requests ADD CONSTRAINT check_enterprise_request_count CHECK (requested_building_count > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_enterprise_request_duration' AND conrelid='public.enterprise_quote_requests'::regclass) THEN ALTER TABLE public.enterprise_quote_requests ADD CONSTRAINT check_enterprise_request_duration CHECK (requested_duration_months IS NULL OR requested_duration_months > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_enterprise_request_status' AND conrelid='public.enterprise_quote_requests'::regclass) THEN ALTER TABLE public.enterprise_quote_requests ADD CONSTRAINT check_enterprise_request_status CHECK (status IN ('New','Contacted','Quoted','Accepted','Rejected','Cancelled')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_quantity' AND conrelid='public.quotations'::regclass) THEN ALTER TABLE public.quotations ADD CONSTRAINT check_quotation_quantity CHECK (quantity > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_billing_purpose' AND conrelid='public.quotations'::regclass) THEN ALTER TABLE public.quotations ADD CONSTRAINT check_quotation_billing_purpose CHECK (billing_purpose IN ('BuildingService','AIUsage')); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_amounts' AND conrelid='public.quotations'::regclass) THEN ALTER TABLE public.quotations ADD CONSTRAINT check_quotation_amounts CHECK (
        unit_price >= 0
        AND subtotal_amount >= 0
        AND tax_amount >= 0
        AND discount_amount >= 0
        AND total_amount = subtotal_amount + tax_amount - discount_amount
        AND total_amount >= 0
    ); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_currency' AND conrelid='public.quotations'::regclass) THEN ALTER TABLE public.quotations ADD CONSTRAINT check_quotation_currency CHECK (currency ~ '^[A-Z]{3}$'); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_issued' AND conrelid='public.quotations'::regclass) THEN ALTER TABLE public.quotations ADD CONSTRAINT check_quotation_issued CHECK (
        status IN ('Draft','Cancelled') OR (issued_by IS NOT NULL AND issued_at IS NOT NULL)
    ); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_quotation_accepted' AND conrelid='public.quotations'::regclass) THEN ALTER TABLE public.quotations ADD CONSTRAINT check_quotation_accepted CHECK (
        (status NOT IN ('Draft','Issued') OR accepted_at IS NULL)
        AND (status <> 'Accepted' OR accepted_at IS NOT NULL)
    ); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payos_order_code' AND conrelid='public.payos_payment_requests'::regclass) THEN ALTER TABLE public.payos_payment_requests ADD CONSTRAINT check_payos_order_code CHECK (order_code > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payos_expected_amount' AND conrelid='public.payos_payment_requests'::regclass) THEN ALTER TABLE public.payos_payment_requests ADD CONSTRAINT check_payos_expected_amount CHECK (expected_amount > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payos_expected_currency' AND conrelid='public.payos_payment_requests'::regclass) THEN ALTER TABLE public.payos_payment_requests ADD CONSTRAINT check_payos_expected_currency CHECK (expected_currency ~ '^[A-Z]{3}$'); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payos_paid_fields' AND conrelid='public.payos_payment_requests'::regclass) THEN ALTER TABLE public.payos_payment_requests ADD CONSTRAINT check_payos_paid_fields CHECK (
        (status = 'Paid' AND paid_transaction_id IS NOT NULL AND paid_at IS NOT NULL)
        OR (status != 'Paid' AND paid_transaction_id IS NULL AND paid_at IS NULL)
    ); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payment_transaction_amount' AND conrelid='public.payment_transactions'::regclass) THEN ALTER TABLE public.payment_transactions ADD CONSTRAINT check_payment_transaction_amount CHECK (received_amount > 0); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payment_transaction_currency' AND conrelid='public.payment_transactions'::regclass) THEN ALTER TABLE public.payment_transactions ADD CONSTRAINT check_payment_transaction_currency CHECK (received_currency ~ '^[A-Z]{3}$'); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payment_transaction_event_id' AND conrelid='public.payment_transactions'::regclass) THEN ALTER TABLE public.payment_transactions ADD CONSTRAINT check_payment_transaction_event_id CHECK (NULLIF(BTRIM(webhook_event_id), '') IS NOT NULL); END IF; END $$;
DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='check_payment_transaction_state_fields' AND conrelid='public.payment_transactions'::regclass) THEN ALTER TABLE public.payment_transactions ADD CONSTRAINT check_payment_transaction_state_fields CHECK (
        (
            status = 'Received'
            AND NOT signature_verified
            AND signature_verified_at IS NULL
            AND processed_at IS NULL
            AND rejection_reason IS NULL
        )
        OR (
            status = 'Verified'
            AND signature_verified
            AND signature_verified_at IS NOT NULL
            AND processed_at IS NULL
            AND rejection_reason IS NULL
        )
        OR (
            status = 'Applied'
            AND signature_verified
            AND signature_verified_at IS NOT NULL
            AND processed_at IS NOT NULL
            AND rejection_reason IS NULL
        )
        OR (
            status = 'Rejected'
            AND signature_verified
            AND signature_verified_at IS NOT NULL
            AND processed_at IS NOT NULL
            AND NULLIF(BTRIM(rejection_reason), '') IS NOT NULL
        )
    ); END IF; END $$;
