-- Additive upgrade for an existing Fire3D v6 database. Run once as schema owner.
-- This does not re-run the v6 bootstrap or create ASP.NET Identity tables.
BEGIN;

CREATE TABLE public.auth_refresh_tokens (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES public.users(id) ON DELETE RESTRICT,
    family_id UUID NOT NULL,
    token_hash VARCHAR(64) NOT NULL UNIQUE CHECK (token_hash ~ '^[0-9a-f]{64}$'),
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL CHECK (expires_at > created_at),
    consumed_at TIMESTAMPTZ CHECK (consumed_at >= created_at),
    revoked_at TIMESTAMPTZ CHECK (revoked_at >= created_at)
);

CREATE INDEX auth_refresh_tokens_user_family ON public.auth_refresh_tokens(user_id, family_id);
CREATE INDEX auth_refresh_tokens_expiry ON public.auth_refresh_tokens(expires_at);
CREATE UNIQUE INDEX auth_refresh_tokens_one_active ON public.auth_refresh_tokens(family_id)
    WHERE consumed_at IS NULL AND revoked_at IS NULL;

COMMENT ON TABLE public.auth_refresh_tokens IS
    'Auth sessions with rotating hashed refresh tokens. Retain consumed tokens until family expiry to detect replay.';

COMMIT;
