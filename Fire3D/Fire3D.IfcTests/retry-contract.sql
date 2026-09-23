-- Test contract extracted verbatim from Docs schema v6.7. Minimal EF fixture dependencies only; not a production migration.

CREATE EXTENSION IF NOT EXISTS pgcrypto;

ALTER TABLE processing_jobs ADD COLUMN IF NOT EXISTS current_attempt_id uuid;

CREATE OR REPLACE FUNCTION fet3d_jsonb_payload_hash(p_payload JSONB)
RETURNS VARCHAR(64)
LANGUAGE sql
IMMUTABLE STRICT
SET search_path = pg_catalog, public
AS $$
    SELECT pg_catalog.encode(
        public.digest(pg_catalog.convert_to(p_payload::TEXT, 'UTF8'), 'sha256'),
        'hex'
    )
$$;

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

CREATE OR REPLACE FUNCTION requeue_processing_job(
    p_job_id UUID, p_idempotency_key TEXT, p_reason TEXT
)
RETURNS TEXT LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog
AS $$
DECLARE
    v_job public.processing_jobs%ROWTYPE;
    v_existing public.integration_outbox_events%ROWTYPE;
    v_key TEXT := NULLIF(pg_catalog.btrim(p_idempotency_key), '');
    v_reason TEXT := NULLIF(pg_catalog.btrim(p_reason), '');
    v_payload JSONB;
    v_payload_hash VARCHAR(64);
    v_organization_id UUID;
    v_enqueue_result TEXT;
BEGIN
    IF p_job_id IS NULL OR v_key IS NULL OR v_reason IS NULL THEN
        RAISE EXCEPTION 'requeue job, idempotency key and reason are required';
    END IF;

    v_payload := pg_catalog.jsonb_build_object('job_id', p_job_id, 'reason', v_reason);
    v_payload_hash := public.fet3d_jsonb_payload_hash(v_payload);
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(v_key, 0));

    SELECT * INTO v_existing
      FROM public.integration_outbox_events
     WHERE idempotency_key = v_key
     FOR UPDATE;
    IF FOUND THEN
        SELECT b.organization_id INTO v_organization_id
          FROM public.processing_jobs AS job
          JOIN public.revisions AS revision ON revision.id = job.revision_id
          JOIN public.buildings AS b ON b.id = revision.building_id
         WHERE job.id = p_job_id;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'processing job does not exist';
        END IF;
        IF v_existing.aggregate_type IS DISTINCT FROM 'ProcessingJob'
           OR v_existing.aggregate_id IS DISTINCT FROM p_job_id
           OR v_existing.event_type IS DISTINCT FROM 'ProcessingJobRequeue'
           OR v_existing.schema_version IS DISTINCT FROM '1'
           OR v_existing.organization_id IS DISTINCT FROM v_organization_id
           OR v_existing.payload IS DISTINCT FROM v_payload
           OR lower(v_existing.payload_hash) IS DISTINCT FROM lower(v_payload_hash) THEN
            RAISE EXCEPTION 'requeue idempotency key conflicts with a different envelope';
        END IF;
        RETURN 'AlreadyRequeued';
    END IF;

    SELECT * INTO v_job FROM public.processing_jobs WHERE id = p_job_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'processing job does not exist'; END IF;
    IF v_job.status = 'Cancelled' OR v_job.status = 'Succeeded' THEN
        RETURN 'NotClaimable';
    END IF;
    IF v_job.status <> 'Failed' THEN
        RETURN 'Conflict';
    END IF;
    UPDATE public.processing_jobs SET status = 'Queued', current_attempt_id = NULL WHERE id = p_job_id;
    v_enqueue_result := public.enqueue_integration_outbox_event_internal(
        v_key, 'ProcessingJob', p_job_id, 'ProcessingJobRequeue', '1', v_payload, false, true
    );
    IF v_enqueue_result <> 'Enqueued' THEN
        RAISE EXCEPTION 'requeue event was not created for a new idempotency key';
    END IF;
    RETURN 'Requeued';
END;
$$;