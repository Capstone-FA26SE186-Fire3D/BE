-- One-time repair for databases whose schema contains the 20260916070926 objects
-- but whose EF migration-history row is missing. Run only after the checks below pass.
BEGIN;

DO $$
DECLARE
    expected_tables text[] := ARRAY[
        'annotation_sets', 'audit_logs', 'auth_refresh_tokens', 'building_contacts',
        'building_floors', 'building_locations', 'buildings', 'debrief_artifacts',
        'feedback', 'invoice_metadata', 'organizations', 'password_reset_tokens',
        'payment_transactions', 'payos_payment_requests', 'processing_jobs', 'quotations',
        'release_packages', 'release_qr_codes', 'releases', 'revision_artifacts',
        'revision_floors', 'revision_issues', 'revision_processing_logs', 'revision_reviews',
        'revisions', 'scenario_versions', 'scenarios', 'service_packages',
        'session_checkpoints', 'session_events', 'session_results', 'sessions',
        'source_documents', 'support_tickets', 'trainings', 'user_devices', 'users',
        'validation_runs'
    ];
BEGIN
    IF EXISTS (
        SELECT 1
        FROM unnest(expected_tables) AS expected(name)
        LEFT JOIN information_schema.tables actual
            ON actual.table_schema = 'public' AND actual.table_name = expected.name
        WHERE actual.table_name IS NULL
    ) THEN
        RAISE EXCEPTION 'Cannot baseline 20260916070926: required tables are missing.';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'users' AND column_name = 'password_hash'
    ) THEN
        RAISE EXCEPTION 'Cannot baseline 20260916070926: users.password_hash is missing.';
    END IF;
END $$;

INSERT INTO public."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT '20260916070926_AddPasswordResetTokens', '10.0.12'
WHERE NOT EXISTS (
    SELECT 1 FROM public."__EFMigrationsHistory"
    WHERE "MigrationId" = '20260916070926_AddPasswordResetTokens'
);

COMMIT;
