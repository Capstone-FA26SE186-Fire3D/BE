-- PostgreSQL canonical JSONB SHA-256; pg_catalog.sha256 avoids assumptions about pgcrypto's installation schema.
CREATE FUNCTION public.fet3d_jsonb_payload_hash(p_payload jsonb) RETURNS varchar(64)
LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog,public
AS $$ SELECT encode(sha256(convert_to(p_payload::text,'UTF8')),'hex') $$;
CREATE TABLE integration_outbox_events (
    idempotency_key VARCHAR(255) PRIMARY KEY,
    aggregate_type VARCHAR(80) NOT NULL,
    aggregate_id UUID NOT NULL,
    event_type VARCHAR(100) NOT NULL,
    schema_version VARCHAR(50) NOT NULL,
    organization_id UUID REFERENCES organizations(id) ON DELETE RESTRICT,
    payload JSONB NOT NULL,
    payload_hash VARCHAR(64) NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Pending', -- Pending | Leased | Published | Failed
    attempts INT NOT NULL DEFAULT 0,
    available_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    lease_owner VARCHAR(255),
    lease_token UUID UNIQUE,
    lease_until TIMESTAMPTZ,
    published_lease_token UUID,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_at TIMESTAMPTZ,
    CONSTRAINT check_outbox_status CHECK (status IN ('Pending','Leased','Published','Failed')),
    CONSTRAINT check_outbox_attempts CHECK (attempts >= 0),
    CONSTRAINT check_outbox_schema_version CHECK (NULLIF(pg_catalog.btrim(schema_version), '') IS NOT NULL),
    CONSTRAINT check_outbox_payload_hash CHECK (
        payload_hash ~ '^[0-9a-fA-F]{64}$'
        AND lower(payload_hash) = public.fet3d_jsonb_payload_hash(payload)
    ),
    CONSTRAINT check_outbox_lease_shape CHECK (
        (
            status = 'Leased'
            AND
            NULLIF(pg_catalog.btrim(lease_owner), '') IS NOT NULL
            AND lease_token IS NOT NULL
            AND lease_until IS NOT NULL
        )
        OR (
            status <> 'Leased'
            AND lease_owner IS NULL
            AND lease_token IS NULL
            AND lease_until IS NULL
        )
    ),
    CONSTRAINT check_outbox_published_shape CHECK (
        (status = 'Published' AND published_at IS NOT NULL AND published_lease_token IS NOT NULL)
        OR (status <> 'Published' AND published_at IS NULL)
    ),
    CONSTRAINT check_outbox_scope CHECK (
        organization_id IS NOT NULL OR aggregate_type IN ('System','Platform')
    )
);

-- Durable consumer deduplication. The row is inserted in the same PostgreSQL
-- transaction as the business effect; Redis ACK happens only after commit.
CREATE TABLE integration_event_consumptions (
    consumer_name VARCHAR(120) NOT NULL,
    event_key VARCHAR(255) REFERENCES integration_outbox_events(idempotency_key) ON DELETE RESTRICT NOT NULL,
    payload_hash VARCHAR(64) NOT NULL,
    result_reference TEXT,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (consumer_name, event_key),
    CONSTRAINT check_event_consumer_name CHECK (NULLIF(pg_catalog.btrim(consumer_name), '') IS NOT NULL),
    CONSTRAINT check_event_consumption_hash CHECK (payload_hash ~ '^[0-9a-fA-F]{64}$')
);

CREATE INDEX idx_outbox_dispatch ON integration_outbox_events(status, available_at, lease_until);
CREATE INDEX idx_outbox_scope_dispatch ON integration_outbox_events(organization_id, status, available_at);
CREATE INDEX idx_event_consumptions_hash ON integration_event_consumptions(event_key, payload_hash);

CREATE OR REPLACE FUNCTION validate_integration_outbox_event_mutation()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.idempotency_key IS NULL
           OR NULLIF(pg_catalog.btrim(NEW.idempotency_key), '') IS NULL
           OR NEW.aggregate_type IS NULL
           OR NULLIF(pg_catalog.btrim(NEW.aggregate_type), '') IS NULL
           OR NEW.aggregate_id IS NULL
           OR NEW.event_type IS NULL
           OR NULLIF(pg_catalog.btrim(NEW.event_type), '') IS NULL
           OR NEW.schema_version IS NULL
           OR NULLIF(pg_catalog.btrim(NEW.schema_version), '') IS NULL
           OR NEW.payload IS NULL
           OR NEW.payload_hash IS NULL
           OR NEW.status IS DISTINCT FROM 'Pending'
           OR NEW.attempts IS DISTINCT FROM 0
           OR NEW.lease_owner IS NOT NULL
           OR NEW.lease_token IS NOT NULL
           OR NEW.lease_until IS NOT NULL
           OR NEW.published_at IS NOT NULL
           OR NEW.published_lease_token IS NOT NULL THEN
            RAISE EXCEPTION 'outbox events must be enqueued as Pending without a lease or publication';
        END IF;
        IF lower(NEW.payload_hash) IS DISTINCT FROM fet3d_jsonb_payload_hash(NEW.payload) THEN
            RAISE EXCEPTION 'outbox payload hash does not match canonical payload';
        END IF;
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.idempotency_key IS DISTINCT FROM NEW.idempotency_key
           OR OLD.aggregate_type IS DISTINCT FROM NEW.aggregate_type
           OR OLD.aggregate_id IS DISTINCT FROM NEW.aggregate_id
           OR OLD.event_type IS DISTINCT FROM NEW.event_type
           OR OLD.schema_version IS DISTINCT FROM NEW.schema_version
           OR OLD.organization_id IS DISTINCT FROM NEW.organization_id
           OR OLD.payload IS DISTINCT FROM NEW.payload
           OR OLD.payload_hash IS DISTINCT FROM NEW.payload_hash
           OR OLD.created_at IS DISTINCT FROM NEW.created_at THEN
            RAISE EXCEPTION 'outbox event identity and payload are immutable after enqueue';
        END IF;
        IF NEW.status = 'Published'
           AND (NEW.published_at IS NULL OR NEW.published_lease_token IS NULL) THEN
            RAISE EXCEPTION 'published outbox events require publication time and completed lease token';
        END IF;
        IF NEW.status <> 'Published' AND NEW.published_at IS NOT NULL THEN
            RAISE EXCEPTION 'non-published outbox events cannot have publication time';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;



CREATE TRIGGER trg_integration_outbox_event_immutable
BEFORE INSERT OR UPDATE ON integration_outbox_events
FOR EACH ROW EXECUTE FUNCTION validate_integration_outbox_event_mutation();

CREATE OR REPLACE FUNCTION enqueue_integration_outbox_event_internal(
    p_idempotency_key TEXT,
    p_aggregate_type TEXT,
    p_aggregate_id UUID,
    p_event_type TEXT,
    p_schema_version TEXT,
    p_payload JSONB,
    p_allow_system BOOLEAN,
    p_allow_requeue BOOLEAN
)
RETURNS TEXT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
AS $$
DECLARE
    v_key TEXT := NULLIF(pg_catalog.btrim(p_idempotency_key), '');
    v_aggregate_type TEXT := NULLIF(pg_catalog.btrim(p_aggregate_type), '');
    v_event_type TEXT := NULLIF(pg_catalog.btrim(p_event_type), '');
    v_schema_version TEXT := NULLIF(pg_catalog.btrim(p_schema_version), '');
    v_organization_id UUID;
    v_payload_hash VARCHAR(64);
    v_existing public.integration_outbox_events%ROWTYPE;
    v_inserted BOOLEAN := false;
    v_inserted_key TEXT;
BEGIN
    IF v_key IS NULL OR v_aggregate_type IS NULL OR p_aggregate_id IS NULL
       OR v_event_type IS NULL OR v_schema_version IS NULL OR p_payload IS NULL
       OR p_allow_system IS NULL OR p_allow_requeue IS NULL
       OR p_allow_system AND p_allow_requeue THEN
        RAISE EXCEPTION 'outbox event identity, schema and payload are required';
    END IF;

    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(v_key, 0));

    IF p_allow_requeue
       AND v_aggregate_type = 'ProcessingJob'
       AND v_event_type = 'ProcessingJobRequeue'
       AND v_schema_version = '1' THEN
        IF NOT p_payload @> pg_catalog.jsonb_build_object('job_id', p_aggregate_id)
           OR NULLIF(pg_catalog.btrim(p_payload ->> 'reason'), '') IS NULL THEN
            RAISE EXCEPTION 'processing requeue payload does not match aggregate';
        END IF;
        SELECT b.organization_id INTO v_organization_id
          FROM public.processing_jobs AS job
          JOIN public.revisions AS revision ON revision.id = job.revision_id
          JOIN public.buildings AS b ON b.id = revision.building_id
         WHERE job.id = p_aggregate_id;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'processing job aggregate does not exist';
        END IF;
    ELSIF p_allow_system
       AND v_aggregate_type IN ('System', 'Platform')
       AND v_event_type IN ('SystemNotification', 'PlatformCacheInvalidation')
       AND v_schema_version = '1' THEN
        v_organization_id := NULL;
    ELSIF NOT p_allow_system
       AND NOT p_allow_requeue
       AND v_aggregate_type = 'ProcessingJob'
       AND v_event_type = 'ProcessingJobRequested'
       AND v_schema_version = '1' THEN
        IF NOT p_payload @> pg_catalog.jsonb_build_object('job_id', p_aggregate_id) THEN
            RAISE EXCEPTION 'processing request payload does not match aggregate';
        END IF;
        SELECT b.organization_id INTO v_organization_id
          FROM public.processing_jobs AS job
          JOIN public.revisions AS revision ON revision.id = job.revision_id
          JOIN public.buildings AS b ON b.id = revision.building_id
         WHERE job.id = p_aggregate_id;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'processing job aggregate does not exist';
        END IF;
    ELSE
        RAISE EXCEPTION 'unsupported outbox event type, schema or enqueue authority';
    END IF;

    v_payload_hash := public.fet3d_jsonb_payload_hash(p_payload);
    INSERT INTO public.integration_outbox_events(
        idempotency_key, aggregate_type, aggregate_id, event_type, schema_version,
        organization_id, payload, payload_hash, status, attempts,
        lease_owner, lease_token, lease_until, published_at, published_lease_token
    ) VALUES (
        v_key, v_aggregate_type, p_aggregate_id, v_event_type, v_schema_version,
        v_organization_id, p_payload, v_payload_hash, 'Pending', 0,
        NULL, NULL, NULL, NULL, NULL
    ) ON CONFLICT (idempotency_key) DO NOTHING
    RETURNING idempotency_key INTO v_inserted_key;
    v_inserted := v_inserted_key IS NOT NULL;

    SELECT * INTO v_existing
      FROM public.integration_outbox_events
     WHERE idempotency_key = v_key
     FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'outbox enqueue could not resolve idempotency record';
    END IF;
    IF v_existing.aggregate_type IS DISTINCT FROM v_aggregate_type
       OR v_existing.aggregate_id IS DISTINCT FROM p_aggregate_id
       OR v_existing.event_type IS DISTINCT FROM v_event_type
       OR v_existing.schema_version IS DISTINCT FROM v_schema_version
       OR v_existing.organization_id IS DISTINCT FROM v_organization_id
       OR v_existing.payload IS DISTINCT FROM p_payload
       OR lower(v_existing.payload_hash) IS DISTINCT FROM lower(v_payload_hash) THEN
        RAISE EXCEPTION 'outbox idempotency key conflicts with a different envelope';
    END IF;
    RETURN CASE WHEN v_inserted THEN 'Enqueued' ELSE 'AlreadyEnqueued' END;
END;
$$;

-- Tenant-scoped backend entry point. System/platform events and requeue events
-- are deliberately rejected here and must use their dedicated gates.
CREATE OR REPLACE FUNCTION enqueue_integration_outbox_event(
    p_idempotency_key TEXT,
    p_aggregate_type TEXT,
    p_aggregate_id UUID,
    p_event_type TEXT,
    p_schema_version TEXT,
    p_payload JSONB
)
RETURNS TEXT
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
AS $$
BEGIN
    RETURN public.enqueue_integration_outbox_event_internal(
        p_idempotency_key, p_aggregate_type, p_aggregate_id,
        p_event_type, p_schema_version, p_payload, false, false
    );
END;
$$;

-- Caller cannot supply a tenant or directly mutate event envelopes.
DO $permissions$
DECLARE target text; client_role text; signature text; original_set boolean; original_inherit boolean; changed_membership boolean; had_create boolean;
BEGIN
  IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_integration_owner') THEN
    CREATE ROLE fet3d_integration_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_integration_owner'
    AND (rolcanlogin OR rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole OR rolreplication)) THEN
    RAISE EXCEPTION 'integration owner must be a restricted NOLOGIN role';
  END IF;
  SELECT set_option,inherit_option INTO original_set,original_inherit FROM pg_auth_members
    WHERE roleid='fet3d_integration_owner'::regrole AND member=current_user::regrole;
  changed_membership := NOT pg_has_role(current_user,'fet3d_integration_owner','SET')
    OR NOT pg_has_role(current_user,'fet3d_integration_owner','USAGE');
  had_create := has_schema_privilege('fet3d_integration_owner','public','CREATE');
  IF changed_membership THEN EXECUTE format('GRANT fet3d_integration_owner TO %I WITH SET TRUE, INHERIT TRUE',current_user); END IF;
  GRANT USAGE,CREATE ON SCHEMA public TO fet3d_integration_owner;
  GRANT SELECT,INSERT,UPDATE ON integration_outbox_events TO fet3d_integration_owner;
  GRANT SELECT,INSERT ON integration_event_consumptions TO fet3d_integration_owner;
  GRANT SELECT ON processing_jobs,revisions,buildings TO fet3d_integration_owner;
  FOREACH target IN ARRAY ARRAY['processing_jobs','revisions','buildings','integration_outbox_events','integration_event_consumptions'] LOOP
    EXECUTE format('CREATE POLICY integration_owner_access ON public.%I TO fet3d_integration_owner USING(true) WITH CHECK(true)',target);
  END LOOP;
  ALTER TABLE integration_outbox_events ENABLE ROW LEVEL SECURITY;
  ALTER TABLE integration_event_consumptions ENABLE ROW LEVEL SECURITY;
  FOREACH target IN ARRAY ARRAY['integration_outbox_events','integration_event_consumptions'] LOOP
    EXECUTE format('REVOKE ALL ON public.%I FROM PUBLIC',target);
    FOREACH client_role IN ARRAY ARRAY['anon','authenticated'] LOOP
      IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client_role) THEN
        EXECUTE format('REVOKE ALL ON public.%I FROM %I',target,client_role);
      END IF;
    END LOOP;
  END LOOP;
  FOREACH signature IN ARRAY ARRAY[
    'public.fet3d_jsonb_payload_hash(jsonb)',
    'public.validate_integration_outbox_event_mutation()',
    'public.enqueue_integration_outbox_event_internal(text,text,uuid,text,text,jsonb,boolean,boolean)',
    'public.enqueue_integration_outbox_event(text,text,uuid,text,text,jsonb)'] LOOP
    EXECUTE format('ALTER FUNCTION %s OWNER TO fet3d_integration_owner',signature);
    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
    FOREACH client_role IN ARRAY ARRAY['anon','authenticated'] LOOP
      IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client_role) THEN
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I',signature,client_role);
      END IF;
    END LOOP;
  END LOOP;
  FOREACH client_role IN ARRAY ARRAY['fire3d_api','fet3d_backend_executor'] LOOP
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=client_role) THEN
      EXECUTE format('GRANT EXECUTE ON FUNCTION public.enqueue_integration_outbox_event(text,text,uuid,text,text,jsonb),public.fet3d_jsonb_payload_hash(jsonb) TO %I',client_role);
      EXECUTE format('GRANT SELECT ON integration_outbox_events,integration_event_consumptions TO %I',client_role);
      FOREACH target IN ARRAY ARRAY['integration_outbox_events','integration_event_consumptions'] LOOP
        EXECUTE format('CREATE POLICY %I ON public.%I FOR SELECT TO %I USING(true)','backend_read_'||client_role,target,client_role);
      END LOOP;
    END IF;
  END LOOP;
  IF NOT had_create THEN REVOKE CREATE ON SCHEMA public FROM fet3d_integration_owner; END IF;
  IF changed_membership THEN
    IF original_set IS NULL THEN EXECUTE format('REVOKE fet3d_integration_owner FROM %I',current_user);
    ELSE EXECUTE format('GRANT fet3d_integration_owner TO %I WITH SET %s, INHERIT %s',current_user,CASE WHEN original_set THEN 'TRUE' ELSE 'FALSE' END,CASE WHEN original_inherit THEN 'TRUE' ELSE 'FALSE' END);
    END IF;
  END IF;
END $permissions$;
