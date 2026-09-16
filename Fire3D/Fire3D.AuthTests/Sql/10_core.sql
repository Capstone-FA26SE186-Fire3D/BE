-- Core v6: metadata and immutable content snapshots. No geometry blobs in SQL.
CREATE TABLE organizations (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), name TEXT NOT NULL CHECK (btrim(name) <> ''),
 slug TEXT UNIQUE NOT NULL CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
 plan VARCHAR(50) NOT NULL DEFAULT 'free', is_active BOOLEAN NOT NULL DEFAULT true,
 metadata JSONB NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(metadata) = 'object'),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(), deleted_at TIMESTAMPTZ
);
CREATE TABLE users (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), organization_id UUID REFERENCES organizations(id) ON DELETE RESTRICT,
 email TEXT NOT NULL CHECK (email = lower(btrim(email)) AND position('@' IN email) > 1),
 password_hash TEXT NOT NULL CHECK (btrim(password_hash) <> ''), full_name TEXT,
 role user_role_enum NOT NULL, is_active BOOLEAN NOT NULL DEFAULT true, last_login_at TIMESTAMPTZ,
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(), deleted_at TIMESTAMPTZ,
 UNIQUE(email), CHECK ((role='OrganizationUser' AND organization_id IS NOT NULL)
 OR (role IN ('PlatformAdmin','Trainee') AND organization_id IS NULL))
);
CREATE TABLE user_devices (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), user_id UUID NOT NULL REFERENCES users(id),
 device_uuid TEXT NOT NULL CHECK (btrim(device_uuid) <> ''), device_model TEXT, os_version TEXT, app_version TEXT,
 last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(), created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 UNIQUE(user_id, device_uuid), UNIQUE(id,user_id)
);
CREATE TABLE buildings (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), organization_id UUID NOT NULL REFERENCES organizations(id),
 name TEXT NOT NULL CHECK (btrim(name) <> ''), building_type TEXT, total_floors INT NOT NULL DEFAULT 1 CHECK(total_floors>0),
 is_active BOOLEAN NOT NULL DEFAULT true, created_by UUID NOT NULL REFERENCES users(id),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(), deleted_at TIMESTAMPTZ,
 UNIQUE(id,organization_id)
);
CREATE TABLE building_locations (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), building_id UUID UNIQUE NOT NULL REFERENCES buildings(id),
 address TEXT, city TEXT, district TEXT, latitude NUMERIC(10,8) CHECK(latitude BETWEEN -90 AND 90),
 longitude NUMERIC(11,8) CHECK(longitude BETWEEN -180 AND 180), geojson JSONB,
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE building_floors (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), building_id UUID NOT NULL, organization_id UUID NOT NULL,
 floor_number INT NOT NULL, floor_name TEXT, floor_plan_url TEXT, area_sqm NUMERIC(10,2) CHECK(area_sqm>=0),
 elevation_meters NUMERIC(10,3), is_basement BOOLEAN NOT NULL DEFAULT false,
 metadata JSONB NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(metadata)='object'),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(building_id,organization_id) REFERENCES buildings(id,organization_id),
 UNIQUE(building_id,floor_number), UNIQUE(id,building_id)
);
CREATE TABLE building_contacts (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), building_id UUID NOT NULL REFERENCES buildings(id),
 contact_name TEXT NOT NULL CHECK(btrim(contact_name)<>''), contact_role TEXT, phone TEXT, email TEXT,
 is_primary BOOLEAN NOT NULL DEFAULT false, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX one_primary_contact ON building_contacts(building_id) WHERE is_primary;
CREATE TABLE revisions (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), building_id UUID NOT NULL, organization_id UUID NOT NULL,
 uploaded_by UUID NOT NULL REFERENCES users(id), version_label TEXT NOT NULL CHECK(btrim(version_label)<>''),
 primary_type file_type_enum NOT NULL DEFAULT 'IFC', status revision_status_enum NOT NULL DEFAULT 'Draft',
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(building_id,organization_id) REFERENCES buildings(id,organization_id),
 UNIQUE(building_id,version_label), UNIQUE(id,organization_id), UNIQUE(id,building_id,organization_id)
);
CREATE TABLE source_documents (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL UNIQUE REFERENCES revisions(id),
 uploaded_by UUID NOT NULL REFERENCES users(id), original_filename TEXT NOT NULL,
 file_type file_type_enum NOT NULL DEFAULT 'IFC', file_size_bytes BIGINT NOT NULL CHECK(file_size_bytes>0),
 storage_url TEXT NOT NULL CHECK(btrim(storage_url)<>''), mime_type TEXT NOT NULL,
 sha256_hash VARCHAR(64) NOT NULL CHECK(sha256_hash ~ '^[0-9a-f]{64}$'),
 quarantine_status quarantine_status_enum NOT NULL DEFAULT 'Pending', quarantine_note TEXT,
 usage_rights TEXT NOT NULL CHECK(btrim(usage_rights)<>''), source_tool TEXT,
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), UNIQUE(id,revision_id)
);
CREATE TABLE annotation_sets (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL REFERENCES revisions(id),
 version_number INT NOT NULL CHECK(version_number>0), data JSONB NOT NULL CHECK(jsonb_typeof(data)='object'),
 provenance TEXT NOT NULL DEFAULT 'manual' CHECK(provenance IN ('manual','pipeline')),
 created_by UUID NOT NULL REFERENCES users(id), created_at TIMESTAMPTZ NOT NULL DEFAULT now(), UNIQUE(revision_id,version_number),
 UNIQUE(id,revision_id)
);
CREATE TABLE processing_jobs (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL REFERENCES revisions(id), source_document_id UUID NOT NULL,
 kind TEXT NOT NULL DEFAULT 'Geometry' CHECK(kind IN ('Geometry','Scenario')), scenario_version_id UUID,
 job_key UUID NOT NULL UNIQUE, status TEXT NOT NULL DEFAULT 'Queued' CHECK(status IN ('Queued','Running','Succeeded','Failed')),
 attempt_number INT NOT NULL DEFAULT 1 CHECK(attempt_number>0), toolchain_version TEXT NOT NULL CHECK(btrim(toolchain_version)<>''),
 lease_owner TEXT, heartbeat_at TIMESTAMPTZ, started_at TIMESTAMPTZ, finished_at TIMESTAMPTZ, error_message TEXT,
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), UNIQUE(id,revision_id),
 FOREIGN KEY(source_document_id,revision_id) REFERENCES source_documents(id,revision_id),
 CHECK((kind='Geometry' AND scenario_version_id IS NULL) OR (kind='Scenario' AND scenario_version_id IS NOT NULL)),
 CHECK((status IN ('Succeeded','Failed')) = (finished_at IS NOT NULL)),
 CHECK(finished_at IS NULL OR (started_at IS NOT NULL AND finished_at>=started_at))
);
CREATE UNIQUE INDEX one_live_revision_job ON processing_jobs(revision_id) WHERE status IN ('Queued','Running');
CREATE TABLE revision_processing_logs (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL REFERENCES revisions(id), job_id UUID NOT NULL,
 step processing_step_enum NOT NULL, status processing_step_status_enum NOT NULL, message TEXT,
 duration_ms INT CHECK(duration_ms>=0), attempt_number INT NOT NULL DEFAULT 1 CHECK(attempt_number>0),
 logged_at TIMESTAMPTZ NOT NULL DEFAULT now(), FOREIGN KEY(job_id,revision_id) REFERENCES processing_jobs(id,revision_id)
);
CREATE TABLE revision_floors (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL, building_id UUID NOT NULL, organization_id UUID NOT NULL,
 building_floor_id UUID NOT NULL, ifc_guid TEXT NOT NULL CHECK(btrim(ifc_guid)<>''),
 floor_number INT NOT NULL, floor_name TEXT NOT NULL, elevation_meters NUMERIC(10,3) NOT NULL,
 coordinate_transform JSONB NOT NULL CHECK(jsonb_typeof(coordinate_transform)='object'),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(revision_id,building_id,organization_id) REFERENCES revisions(id,building_id,organization_id),
 FOREIGN KEY(building_floor_id,building_id) REFERENCES building_floors(id,building_id),
 UNIQUE(revision_id,ifc_guid), UNIQUE(revision_id,building_floor_id), UNIQUE(id,revision_id)
);
CREATE TABLE scenarios (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), building_id UUID NOT NULL, organization_id UUID NOT NULL,
 name TEXT NOT NULL CHECK(btrim(name)<>''), created_by UUID NOT NULL REFERENCES users(id),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(building_id,organization_id) REFERENCES buildings(id,organization_id), UNIQUE(id,building_id,organization_id)
);
CREATE TABLE scenario_versions (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), scenario_id UUID NOT NULL, revision_id UUID NOT NULL,
 building_id UUID NOT NULL, organization_id UUID NOT NULL, version_number INT NOT NULL CHECK(version_number>0),
 name TEXT NOT NULL CHECK(btrim(name)<>''), schema_version TEXT NOT NULL DEFAULT '1.0',
 algorithm_version TEXT NOT NULL CHECK(btrim(algorithm_version)<>''), random_seed BIGINT NOT NULL,
 time_limit_seconds INT NOT NULL CHECK(time_limit_seconds BETWEEN 1 AND 3600),
 spawn_config JSONB NOT NULL CHECK(jsonb_typeof(spawn_config)='object' AND spawn_config ? 'nodeId'),
 goal_config JSONB NOT NULL CHECK(jsonb_typeof(goal_config)='object' AND goal_config ? 'exitIds'),
 fire_source_config JSONB NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(fire_source_config)='object'),
 npc_config JSONB NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(npc_config)='object'),
 blocked_elements JSONB NOT NULL DEFAULT '[]' CHECK(jsonb_typeof(blocked_elements)='array'),
 routing_config JSONB NOT NULL CHECK(jsonb_typeof(routing_config)='object'),
 scoring_config JSONB NOT NULL CHECK(jsonb_typeof(scoring_config)='object'),
 mode_policy JSONB NOT NULL CHECK(jsonb_typeof(mode_policy)='object' AND mode_policy ?& ARRAY['Learn','Guided','Assessment']),
 safety_thresholds JSONB NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(safety_thresholds)='object'),
 replan_interval_seconds INT NOT NULL DEFAULT 5 CHECK(replan_interval_seconds>0),
 scenario_hash VARCHAR(64) NOT NULL CHECK(scenario_hash ~ '^[0-9a-f]{64}$'),
 created_by UUID NOT NULL REFERENCES users(id), created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(scenario_id,building_id,organization_id) REFERENCES scenarios(id,building_id,organization_id),
 FOREIGN KEY(revision_id,building_id,organization_id) REFERENCES revisions(id,building_id,organization_id),
 UNIQUE(scenario_id,version_number), UNIQUE(id,revision_id,organization_id)
);
CREATE TABLE revision_artifacts (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL REFERENCES revisions(id), job_id UUID NOT NULL,
 artifact_type TEXT NOT NULL CHECK(artifact_type IN ('Preview','Geometry','Graph','HazardGrid','CandidatePackage')),
 object_key TEXT NOT NULL CHECK(btrim(object_key)<>''), sha256_hash VARCHAR(64) NOT NULL CHECK(sha256_hash ~ '^[0-9a-f]{64}$'),
 size_bytes BIGINT NOT NULL CHECK(size_bytes>0), schema_version TEXT NOT NULL CHECK(btrim(schema_version)<>''),
 metadata JSONB NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(metadata)='object'), created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(job_id,revision_id) REFERENCES processing_jobs(id,revision_id), UNIQUE(id,revision_id), UNIQUE(job_id,artifact_type,sha256_hash)
);
ALTER TABLE processing_jobs ADD FOREIGN KEY(scenario_version_id) REFERENCES scenario_versions(id);
CREATE TABLE validation_runs (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL REFERENCES revisions(id), job_id UUID NOT NULL UNIQUE,
 kind TEXT NOT NULL CHECK(kind IN ('Geometry','Scenario')), scenario_version_id UUID,
 candidate_artifact_id UUID, annotation_set_id UUID,
 outcome TEXT NOT NULL CHECK(outcome IN ('Passed','Failed')), scenario_hash VARCHAR(64),
 validator_version TEXT NOT NULL CHECK(btrim(validator_version)<>''), report JSONB NOT NULL CHECK(jsonb_typeof(report)='object'),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(job_id,revision_id) REFERENCES processing_jobs(id,revision_id),
 FOREIGN KEY(candidate_artifact_id,revision_id) REFERENCES revision_artifacts(id,revision_id),
 FOREIGN KEY(annotation_set_id,revision_id) REFERENCES annotation_sets(id,revision_id),
 FOREIGN KEY(scenario_version_id) REFERENCES scenario_versions(id),
 CHECK((kind='Geometry' AND scenario_version_id IS NULL AND scenario_hash IS NULL AND candidate_artifact_id IS NULL)
 OR (kind='Scenario' AND scenario_version_id IS NOT NULL AND candidate_artifact_id IS NOT NULL
 AND scenario_hash IS NOT NULL AND scenario_hash ~ '^[0-9a-f]{64}$'))
);
CREATE TABLE revision_issues (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), validation_run_id UUID NOT NULL REFERENCES validation_runs(id),
 severity TEXT NOT NULL CHECK(severity IN ('Info','Warning','Error')), code TEXT NOT NULL, ifc_guid TEXT,
 message TEXT NOT NULL CHECK(btrim(message)<>''), details JSONB NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(details)='object'),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE revision_reviews (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL REFERENCES revisions(id),
 scenario_version_id UUID NOT NULL REFERENCES scenario_versions(id), reviewed_by UUID NOT NULL REFERENCES users(id),
 validation_run_id UUID NOT NULL REFERENCES validation_runs(id), annotation_set_id UUID,
 action review_action_enum NOT NULL, review_message TEXT, reviewed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(annotation_set_id,revision_id) REFERENCES annotation_sets(id,revision_id),
 CHECK(action<>'Rejected' OR NULLIF(btrim(review_message),'') IS NOT NULL),
 UNIQUE(validation_run_id,action)
);
CREATE UNIQUE INDEX one_scenario_confirmation ON revision_reviews(scenario_version_id) WHERE action='ConfirmForTraining';
CREATE TABLE releases (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), revision_id UUID NOT NULL, scenario_version_id UUID NOT NULL,
 building_id UUID NOT NULL, organization_id UUID NOT NULL, confirmation_review_id UUID NOT NULL REFERENCES revision_reviews(id),
 published_by UUID REFERENCES users(id), revoked_by UUID REFERENCES users(id), status release_status_enum NOT NULL DEFAULT 'Built',
 safety_thresholds JSONB NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(safety_thresholds)='object'),
 revoked_reason TEXT, published_at TIMESTAMPTZ, revoked_at TIMESTAMPTZ,
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(revision_id,building_id,organization_id) REFERENCES revisions(id,building_id,organization_id),
 FOREIGN KEY(scenario_version_id,revision_id,organization_id) REFERENCES scenario_versions(id,revision_id,organization_id),
 UNIQUE(revision_id,scenario_version_id), UNIQUE(id,scenario_version_id,organization_id),
 CHECK((published_at IS NULL)=(published_by IS NULL)),
 CHECK(status NOT IN ('Published','Superseded') OR published_at IS NOT NULL),
 CHECK(status<>'Built' OR published_at IS NULL),
 CHECK((status='Revoked' AND revoked_by IS NOT NULL AND revoked_at IS NOT NULL AND NULLIF(btrim(revoked_reason),'') IS NOT NULL)
 OR (status<>'Revoked' AND revoked_by IS NULL AND revoked_at IS NULL AND revoked_reason IS NULL))
);
CREATE TABLE release_packages (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), release_id UUID UNIQUE NOT NULL REFERENCES releases(id),
 candidate_artifact_id UUID NOT NULL REFERENCES revision_artifacts(id), manifest_url TEXT NOT NULL CHECK(btrim(manifest_url)<>''),
 manifest_sha256 VARCHAR(64) NOT NULL CHECK(manifest_sha256 ~ '^[0-9a-f]{64}$'),
 package_url TEXT NOT NULL CHECK(btrim(package_url)<>''), checksum_sha256 VARCHAR(64) NOT NULL CHECK(checksum_sha256 ~ '^[0-9a-f]{64}$'),
 package_size_bytes BIGINT NOT NULL CHECK(package_size_bytes>0), min_runtime_version TEXT NOT NULL CHECK(btrim(min_runtime_version)<>''),
 schema_version TEXT NOT NULL DEFAULT '1.0', build_target TEXT NOT NULL DEFAULT 'Android' CHECK(build_target='Android'),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE trainings (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), release_id UUID NOT NULL, scenario_version_id UUID NOT NULL, organization_id UUID NOT NULL,
 name TEXT NOT NULL CHECK(btrim(name)<>''), description TEXT, start_date TIMESTAMPTZ, end_date TIMESTAMPTZ,
 status training_status_enum NOT NULL DEFAULT 'Draft', mode session_mode_enum NOT NULL DEFAULT 'Guided',
 allowed_modes TEXT[] NOT NULL DEFAULT ARRAY['Learn','Guided','Assessment'], max_attempts INT CHECK(max_attempts>0),
 created_by UUID NOT NULL REFERENCES users(id), created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(release_id,scenario_version_id,organization_id) REFERENCES releases(id,scenario_version_id,organization_id),
 UNIQUE(id,release_id,scenario_version_id,organization_id), UNIQUE(id,release_id,organization_id),
 CHECK(start_date IS NULL OR end_date IS NULL OR end_date>start_date),
 CHECK(cardinality(allowed_modes)>0 AND array_position(allowed_modes,NULL) IS NULL
 AND allowed_modes <@ ARRAY['Learn','Guided','Assessment']::TEXT[] AND mode::TEXT=ANY(allowed_modes))
);
CREATE TABLE release_qr_codes (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), release_id UUID NOT NULL, training_id UUID NOT NULL, organization_id UUID NOT NULL,
 revision_floor_id UUID REFERENCES revision_floors(id), created_by UUID NOT NULL REFERENCES users(id),
 qr_hash VARCHAR(64) NOT NULL UNIQUE CHECK(qr_hash ~ '^[0-9a-f]{64}$'), label TEXT, expires_at TIMESTAMPTZ,
 is_active BOOLEAN NOT NULL DEFAULT true, created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 FOREIGN KEY(training_id,release_id,organization_id) REFERENCES trainings(id,release_id,organization_id),
 UNIQUE(id,training_id,release_id,organization_id)
);
CREATE TABLE sessions (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), training_id UUID NOT NULL, release_id UUID NOT NULL,
 scenario_version_id UUID NOT NULL, organization_id UUID NOT NULL, trainee_user_id UUID NOT NULL REFERENCES users(id),
 device_id UUID NOT NULL, qr_code_id UUID NOT NULL, start_key UUID NOT NULL,
 app_version TEXT NOT NULL, unity_version TEXT NOT NULL, protocol_version TEXT NOT NULL DEFAULT '1.0',
 scenario_hash VARCHAR(64) NOT NULL CHECK(scenario_hash ~ '^[0-9a-f]{64}$'),
 release_hash VARCHAR(64) NOT NULL CHECK(release_hash ~ '^[0-9a-f]{64}$'),
 mode session_mode_enum NOT NULL, status session_status_enum NOT NULL DEFAULT 'Created',
 started_at TIMESTAMPTZ NOT NULL DEFAULT now(), launched_at TIMESTAMPTZ, ended_at TIMESTAMPTZ,
 terminal_reason TEXT, content_status_at_completion release_status_enum,
 FOREIGN KEY(device_id,trainee_user_id) REFERENCES user_devices(id,user_id),
 FOREIGN KEY(training_id,release_id,scenario_version_id,organization_id) REFERENCES trainings(id,release_id,scenario_version_id,organization_id),
 FOREIGN KEY(qr_code_id,training_id,release_id,organization_id) REFERENCES release_qr_codes(id,training_id,release_id,organization_id),
 UNIQUE(trainee_user_id,start_key),
 CHECK(launched_at IS NULL OR launched_at>=started_at), CHECK(ended_at IS NULL OR ended_at>=started_at),
 CHECK((status IN ('Created','Launching','Running') AND ended_at IS NULL AND content_status_at_completion IS NULL)
 OR (status NOT IN ('Created','Launching','Running') AND ended_at IS NOT NULL AND content_status_at_completion IS NOT NULL)),
 CHECK(status NOT IN ('Running','Completed','CompletedWithSupersededRelease','ScenarioUnsurvivable') OR launched_at IS NOT NULL)
);
CREATE TABLE session_events (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), session_id UUID NOT NULL REFERENCES sessions(id),
 sequence_number BIGINT NOT NULL CHECK(sequence_number>0), client_event_id UUID NOT NULL,
 schema_version TEXT NOT NULL DEFAULT '1.0', event_type TEXT NOT NULL CHECK(btrim(event_type)<>''),
 event_data JSONB NOT NULL DEFAULT '{}' CHECK(jsonb_typeof(event_data)='object'), elapsed_ms BIGINT NOT NULL CHECK(elapsed_ms>=0),
 recorded_at TIMESTAMPTZ NOT NULL, received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 UNIQUE(session_id,sequence_number), UNIQUE(session_id,client_event_id)
);
CREATE TABLE session_results (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), session_id UUID NOT NULL UNIQUE REFERENCES sessions(id),
 completion_key UUID NOT NULL, last_event_sequence BIGINT NOT NULL CHECK(last_event_sequence>=0),
 submission_payload JSONB NOT NULL CHECK(jsonb_typeof(submission_payload)='object'),
 result_schema_version TEXT NOT NULL DEFAULT '1.0', rubric_version TEXT NOT NULL CHECK(btrim(rubric_version)<>''),
 score NUMERIC(5,2) NOT NULL CHECK(score BETWEEN 0 AND 100), time_taken_seconds INT NOT NULL CHECK(time_taken_seconds>=0),
 wrong_exits INT NOT NULL DEFAULT 0 CHECK(wrong_exits>=0), hazard_exposure_score NUMERIC(12,2) NOT NULL DEFAULT 0 CHECK(hazard_exposure_score>=0),
 total_distance_meters NUMERIC(12,2) NOT NULL DEFAULT 0 CHECK(total_distance_meters>=0), reached_exit BOOLEAN NOT NULL,
 exit_point_id TEXT, path_traveled JSONB NOT NULL DEFAULT '[]' CHECK(jsonb_typeof(path_traveled)='array'),
 client_started_at TIMESTAMPTZ, client_ended_at TIMESTAMPTZ, synced_at TIMESTAMPTZ NOT NULL DEFAULT now(),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), CHECK(NOT reached_exit OR NULLIF(btrim(exit_point_id),'') IS NOT NULL),
 CHECK(client_started_at IS NULL OR client_ended_at IS NULL OR client_ended_at>=client_started_at)
);
CREATE TABLE session_checkpoints (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), session_id UUID NOT NULL REFERENCES sessions(id),
 sequence_number INT NOT NULL CHECK(sequence_number>0), player_transform JSONB NOT NULL CHECK(jsonb_typeof(player_transform)='object'),
 player_status JSONB NOT NULL DEFAULT '{}', world_interactive_states JSONB NOT NULL DEFAULT '{}',
 hazard_time_step INT NOT NULL CHECK(hazard_time_step>=0), npc_states JSONB NOT NULL DEFAULT '[]', active_objectives JSONB NOT NULL DEFAULT '[]',
 release_hash VARCHAR(64) NOT NULL CHECK(release_hash ~ '^[0-9a-f]{64}$'), scenario_hash VARCHAR(64) NOT NULL CHECK(scenario_hash ~ '^[0-9a-f]{64}$'),
 created_at TIMESTAMPTZ NOT NULL DEFAULT now(), UNIQUE(session_id,sequence_number)
);
CREATE TABLE debrief_artifacts (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), session_id UUID UNIQUE NOT NULL REFERENCES session_results(session_id),
 trajectory_heatmap JSONB NOT NULL DEFAULT '{}', optimal_path JSONB NOT NULL DEFAULT '[]', wrong_decisions JSONB NOT NULL DEFAULT '[]',
 hazard_timeline JSONB NOT NULL DEFAULT '[]', npc_summary JSONB NOT NULL DEFAULT '{}',
 is_visible_to_trainee BOOLEAN NOT NULL DEFAULT true, generator_version TEXT NOT NULL,
 generated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE audit_logs (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), user_id UUID, organization_id UUID,
 actor_type TEXT NOT NULL DEFAULT 'User' CHECK(actor_type IN ('User','Worker','System')),
 action audit_action_enum NOT NULL, target_entity TEXT NOT NULL, target_id UUID,
 correlation_id UUID NOT NULL DEFAULT gen_random_uuid(), old_values JSONB, new_values JSONB,
 ip_address INET, user_agent TEXT, created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX sessions_personal_history ON sessions(trainee_user_id,started_at DESC);
CREATE INDEX sessions_org_history ON sessions(organization_id,started_at DESC);
CREATE INDEX sessions_training_attempts ON sessions(training_id,trainee_user_id,mode);
CREATE INDEX sessions_release ON sessions(release_id);
CREATE INDEX qr_release ON release_qr_codes(release_id);
CREATE INDEX qr_training ON release_qr_codes(training_id);
CREATE INDEX trainings_release ON trainings(release_id);
CREATE INDEX artifacts_revision ON revision_artifacts(revision_id);
CREATE INDEX validation_revision ON validation_runs(revision_id,kind,outcome);
CREATE INDEX audit_org_history ON audit_logs(organization_id,created_at DESC);
COMMENT ON TABLE source_documents IS 'Phase 1: one accepted source IFC per revision; storage_url is a private stable object key, never a presigned URL.';
COMMENT ON TABLE scenarios IS 'Logical training scenario belonging to a building; versions may pin different compatible building revisions.';
COMMENT ON TABLE scenario_versions IS 'Append-only snapshot. Confirmation is per version through revision_reviews, not an exclusive lock on all scenarios of the revision.';
COMMENT ON TABLE revision_floors IS 'Immutable floor snapshot used by content, QR labels and historical replay; changes require a new revision.';
COMMENT ON TABLE validation_runs IS 'Append-only attestation from a trusted validator; SQL checks bindings, not geometry, routing or cryptographic file contents.';
COMMENT ON COLUMN trainings.max_attempts IS 'NULL = unlimited. Positive value limits Assessment session creation per Trainee and Training, including launch failures; Learn/Guided unlimited.';
COMMENT ON COLUMN release_qr_codes.qr_hash IS 'SHA-256 of a high-entropy opaque token. Original token returned once for printing; rotation creates a new row.';
COMMENT ON COLUMN release_qr_codes.revision_floor_id IS 'Placement metadata only; does not override the immutable scenario spawn.';
COMMENT ON TABLE session_results IS 'One immutable server-accepted result per session. Backend validates/calculates rubric before insert; no client score trust implied.';
