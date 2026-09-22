START TRANSACTION;
ALTER TABLE users DROP COLUMN password_hash;

ALTER TABLE processing_jobs DROP COLUMN attempt_number;

ALTER TABLE revision_processing_logs RENAME COLUMN attempt_number TO "AttemptNumber";

ALTER TABLE processing_jobs RENAME COLUMN toolchain_version TO input_hash;

ALTER TABLE users ADD firebase_uid character varying(128);

ALTER TABLE user_devices ADD fcm_token character varying(255);

ALTER TABLE revision_processing_logs ALTER COLUMN "AttemptNumber" DROP DEFAULT;

CREATE TABLE runtime_compatibility_catalog (
    id uuid NOT NULL,
    runtime_version text NOT NULL,
    protocol_version text NOT NULL,
    manifest_schema_version text NOT NULL,
    capabilities jsonb NOT NULL,
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_runtime_compatibility_catalog" PRIMARY KEY (id)
);

CREATE TABLE scenario_drafts (
    id uuid NOT NULL,
    scenario_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    organization_id uuid NOT NULL,
    building_id uuid NOT NULL,
    draft_number integer NOT NULL,
    state jsonb NOT NULL,
    source text NOT NULL,
    last_ai_request_id uuid,
    created_by uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    updated_at timestamp with time zone NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_scenario_drafts" PRIMARY KEY (id),
    CONSTRAINT "FK_scenario_drafts_buildings_building_id" FOREIGN KEY (building_id) REFERENCES buildings (id) ON DELETE CASCADE,
    CONSTRAINT "FK_scenario_drafts_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT "FK_scenario_drafts_revisions_revision_id" FOREIGN KEY (revision_id) REFERENCES revisions (id) ON DELETE CASCADE,
    CONSTRAINT "FK_scenario_drafts_scenarios_scenario_id" FOREIGN KEY (scenario_id) REFERENCES scenarios (id) ON DELETE CASCADE,
    CONSTRAINT "FK_scenario_drafts_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE CASCADE
);

CREATE TABLE playtest_sessions (
    id uuid NOT NULL,
    organization_id uuid NOT NULL,
    building_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    scenario_draft_id uuid,
    scenario_version_id uuid NOT NULL,
    service_entitlement_id uuid NOT NULL,
    created_by uuid NOT NULL,
    package_hash text NOT NULL,
    protocol_version text NOT NULL,
    manifest_schema_version text NOT NULL,
    prepare_idempotency_key text,
    runtime_version text,
    start_idempotency_key text,
    status text NOT NULL,
    completion_idempotency_key text,
    created_at timestamp with time zone NOT NULL DEFAULT (now()),
    started_at timestamp with time zone,
    ended_at timestamp with time zone,
    CONSTRAINT "PK_playtest_sessions" PRIMARY KEY (id),
    CONSTRAINT "FK_playtest_sessions_buildings_building_id" FOREIGN KEY (building_id) REFERENCES buildings (id) ON DELETE CASCADE,
    CONSTRAINT "FK_playtest_sessions_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT "FK_playtest_sessions_revisions_revision_id" FOREIGN KEY (revision_id) REFERENCES revisions (id) ON DELETE CASCADE,
    CONSTRAINT "FK_playtest_sessions_scenario_drafts_scenario_draft_id" FOREIGN KEY (scenario_draft_id) REFERENCES scenario_drafts (id),
    CONSTRAINT "FK_playtest_sessions_scenario_versions_scenario_version_id" FOREIGN KEY (scenario_version_id) REFERENCES scenario_versions (id) ON DELETE CASCADE,
    CONSTRAINT "FK_playtest_sessions_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE CASCADE
);

CREATE INDEX "IX_playtest_sessions_building_id" ON playtest_sessions (building_id);

CREATE INDEX "IX_playtest_sessions_created_by" ON playtest_sessions (created_by);

CREATE INDEX "IX_playtest_sessions_organization_id" ON playtest_sessions (organization_id);

CREATE INDEX "IX_playtest_sessions_revision_id" ON playtest_sessions (revision_id);

CREATE INDEX "IX_playtest_sessions_scenario_draft_id" ON playtest_sessions (scenario_draft_id);

CREATE INDEX "IX_playtest_sessions_scenario_version_id" ON playtest_sessions (scenario_version_id);

CREATE INDEX "IX_scenario_drafts_building_id" ON scenario_drafts (building_id);

CREATE INDEX "IX_scenario_drafts_created_by" ON scenario_drafts (created_by);

CREATE INDEX "IX_scenario_drafts_organization_id" ON scenario_drafts (organization_id);

CREATE INDEX "IX_scenario_drafts_revision_id" ON scenario_drafts (revision_id);

CREATE INDEX "IX_scenario_drafts_scenario_id" ON scenario_drafts (scenario_id);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260921044311_UpdateModels_Module3_Module4', '10.0.12');

COMMIT;

