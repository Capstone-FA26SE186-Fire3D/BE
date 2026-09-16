-- Generated fresh-project baseline. No application data or passwords.
-- Apply atomically with Supabase CLI OR psql --single-transaction -v ON_ERROR_STOP=1.
SET LOCAL search_path = public, extensions, pg_catalog;
DO $preflight$
DECLARE n text;
BEGIN
 IF current_setting('server_version_num')::integer < 170000 THEN
  RAISE EXCEPTION 'Fire3D baseline requires PostgreSQL 17 or newer';
 END IF;
 FOREACH n IN ARRAY ARRAY['organizations','users','user_devices','buildings','building_locations','building_floors','building_contacts','revisions','source_documents','annotation_sets','processing_jobs','revision_processing_logs','revision_floors','scenarios','scenario_versions','revision_artifacts','validation_runs','revision_issues','revision_reviews','releases','release_packages','trainings','release_qr_codes','sessions','session_events','session_results','session_checkpoints','debrief_artifacts','audit_logs','service_packages','quotations','payos_payment_requests','payment_transactions','invoice_metadata','feedback','support_tickets','auth_refresh_tokens'] LOOP
  IF to_regclass('public.' || n) IS NOT NULL THEN
   RAISE EXCEPTION 'Fresh-project baseline only: public.% already exists', n;
  END IF;
 END LOOP;
 FOREACH n IN ARRAY ARRAY['fet3d_payos_ledger_owner','fet3d_payos_request_executor','fet3d_payos_webhook_executor','fire3d_api'] LOOP
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=n) THEN
   RAISE EXCEPTION 'Role % already exists; review its ownership/permissions before migration', n;
  END IF;
 END LOOP;
END $preflight$;
CREATE SCHEMA IF NOT EXISTS extensions;
CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA extensions;

-- SOURCE: 00_types.sql
-- pgcrypto installed above; existing extension location is preserved.

-- ==============================================================================
-- SECTION 1: ENUM TYPES
-- ==============================================================================

-- Phân quyền người dùng trong hệ thống Multi-tenant
CREATE TYPE user_role_enum AS ENUM (
    'PlatformAdmin',    -- Quản trị viên toàn hệ thống
    'OrganizationUser', -- Sở hữu Building, IFC, scenario, publish, QR, analytics và billing
    'Trainee'           -- Học viên đã xác thực tham gia qua QR
);

-- IFC là định dạng source duy nhất của pipeline
CREATE TYPE file_type_enum AS ENUM (
    'IFC'               -- Industry Foundation Classes
);

-- Vòng đời xử lý phiên bản BIM (Revision Pipeline)
CREATE TYPE revision_status_enum AS ENUM (
    'Draft',            -- Revision mới tạo, chưa upload file
    'Uploaded',         -- File đã lên server, chờ worker xử lý
    'Processing',       -- Worker đang chạy pipeline tự động
    'NeedsFix',         -- Worker xong, có issue cần OrganizationUser sửa
    'ReadyForScenario', -- IFC và connectivity QA đạt, có thể author scenario
    'ConfirmedForTraining', -- ConfirmForTraining đã ghi nhận readiness nội bộ
    'Rejected',         -- OrganizationUser từ chối revision
    'Failed',           -- Worker lỗi sau tất cả lượt retry
    'Superseded'        -- Đã có revision mới hơn thay thế
);

-- Hành động review readiness do OrganizationUser thực hiện; không phải chứng nhận PCCC
CREATE TYPE review_action_enum AS ENUM (
    'ConfirmForTraining', -- Chuyển ReadyForScenario thành ConfirmedForTraining
    'Rejected'          -- Cần chỉnh sửa source/scenario kèm lý do
);

-- Trạng thái bản phát hành gói 3D tòa nhà (Release)
CREATE TYPE release_status_enum AS ENUM (
    'Built',            -- Đã đóng gói bundle 3D thành công
    'Published',        -- Bản phát hành chính thức có thể được QR active sử dụng
    'Superseded',       -- Đã có release mới hơn thay thế
    'Revoked'           -- Bị thu hồi khẩn cấp do phát hiện lỗi nghiêm trọng
);

-- Trạng thái hoạt động; Active là một điều kiện mở Session mới
CREATE TYPE training_status_enum AS ENUM (
    'Draft',            -- Hoạt động mới tạo
    'Active',           -- Hoạt động đang vận hành
    'Closed',           -- Hoạt động đã kết thúc
    'Archived'          -- Hoạt động đã lưu trữ
);

-- Trạng thái kiểm tra an toàn file upload (Antivirus Quarantine)
CREATE TYPE quarantine_status_enum AS ENUM (
    'Pending',          -- Đang chờ quét virus/mã độc
    'Accepted',         -- File an toàn, cho phép đưa vào pipeline
    'Rejected'          -- Phát hiện mã độc/file lỗi, bị chặn
);

-- Chế độ của phiên diễn tập
CREATE TYPE session_mode_enum AS ENUM (
    'Learn',            -- Chế độ tự do tham quan & học tập sơ đồ
    'Guided',           -- Chế độ luyện tập có mũi tên/trợ giúp dẫn đường
    'Assessment'        -- Chế độ đánh giá trong mô phỏng (theo policy trợ giúp)
);

-- Trạng thái phiên diễn tập của người chơi
CREATE TYPE session_status_enum AS ENUM (
    'Created',                  -- Khởi tạo phiên thành công
    'Launching',                -- Đang tải bản đồ 3D & kịch bản vào game
    'Running',                  -- Đang trong quá trình di chuyển thoát nạn
    'Completed',                -- Hoàn thành mục tiêu bài tập mô phỏng
    'CompletedWithSupersededRelease', -- Hoàn thành trên release đã bị thay thế giữa phiên
    'ScenarioUnsurvivable',     -- Nhân vật bị kẹt/ngạt khói tử vong trong game
    'Aborted',                  -- Người chơi chủ động thoát giữa chừng
    'Abandoned',                -- Treo game quá lâu không tương tác
    'Crashed'                   -- Mất kết nối/Sập ứng dụng
);

-- Hành động ghi nhật ký hệ thống (Audit Trail)
CREATE TYPE audit_action_enum AS ENUM (
    'Upload',           -- Tải file tài liệu/bản vẽ lên
    'ConfirmForTraining', -- Ghi nhận transition readiness nội bộ
    'Reject',           -- Từ chối nội dung
    'Publish',          -- Xuất bản bản phát hành mới
    'Revoke',           -- Thu hồi bản phát hành
    'Sync',             -- Đồng bộ dữ liệu offline từ Mobile
    'Login',            -- Đăng nhập hệ thống
    'Logout',           -- Đăng xuất hệ thống
    'Download',         -- Tải xuống tài nguyên/bản vẽ
    'Delete',           -- Xóa dữ liệu
    'Create',           -- Tạo mới dữ liệu
    'Update',           -- Cập nhật dữ liệu
    'Rollback',         -- Khôi phục phiên bản cũ
    'Grant',            -- Cấp quyền truy cập
    'Resume',           -- Khôi phục phiên chơi từ Checkpoint
    'Payment',          -- Xử lý quotation, PayOS hoặc invoice metadata
    'Support'           -- Xử lý feedback hoặc support ticket
);

-- Các bước trong Pipeline xử lý tự động file BIM/3D
CREATE TYPE processing_step_enum AS ENUM (
    'Quarantine',       -- Bước 1: Quét virus & mã độc
    'Parse',            -- Bước 2: Đọc cấu trúc hình học và thuộc tính IFC
    'CleanGeometry',    -- Bước 3: Làm sạch lưới 3D, tối ưu polygon
    'Decimate',         -- Bước 4: Giảm dung lượng mô hình cho Mobile
    'GenNavMesh',       -- Bước 5: Tạo lưới di chuyển NavMesh cho AI/Player
    'GenHazardGrid',    -- Bước 6: Chia lưới tọa độ mô phỏng cháy & khói
    'ExportGLB',        -- Bước 7: Xuất định dạng 3D chuẩn GLB/gTF
    'PackageBundle'     -- Bước 8: Đóng gói AssetBundle & tạo Manifest
);

-- Trạng thái từng bước xử lý trong Pipeline
CREATE TYPE processing_step_status_enum AS ENUM (
    'Started',          -- Bắt đầu thực thi bước
    'Success',          -- Xử lý hoàn tất thành công
    'Failed'            -- Xử lý thất bại
);

-- Phase 2: vòng đời quotation và thanh toán
CREATE TYPE quotation_status_enum AS ENUM (
    'Draft',
    'Issued',
    'Accepted',
    'Expired',
    'Cancelled'
);

CREATE TYPE payment_request_status_enum AS ENUM (
    'Pending',
    'Paid',
    'Expired',
    'Cancelled',
    'Failed'
);

CREATE TYPE payment_transaction_status_enum AS ENUM (
    'Received',
    'Verified',
    'Rejected',
    'Applied'
);

CREATE TYPE feedback_status_enum AS ENUM (
    'Submitted',
    'Reviewed',
    'Closed'
);

CREATE TYPE support_ticket_status_enum AS ENUM (
    'Open',
    'InProgress',
    'Resolved',
    'Closed'
);

CREATE TYPE support_priority_enum AS ENUM (
    'Low',
    'Normal',
    'High',
    'Urgent'
);

-- ==============================================================================
-- SECTION 2: COMMON FUNCTIONS
-- ==============================================================================

CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ language 'plpgsql';



-- SOURCE: 10_core.sql
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


-- SOURCE: 20_functions.sql
-- Functions are SECURITY INVOKER unless explicitly documented otherwise.
-- Browser/mobile never connect to DB. API authenticates actor; these checks do not authenticate UUID arguments.
CREATE FUNCTION assert_org_user(p_user UUID, p_org UUID) RETURNS VOID LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM users u JOIN organizations o ON o.id=u.organization_id
 WHERE u.id=p_user AND u.role='OrganizationUser' AND u.organization_id=p_org
 AND u.is_active AND u.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL)
 THEN RAISE EXCEPTION 'Active OrganizationUser and organization required'; END IF;
END $$;
CREATE FUNCTION lock_content(p_building UUID) RETURNS VOID LANGUAGE sql AS $$
 SELECT pg_advisory_xact_lock(hashtextextended(p_building::text,601));
$$;
CREATE FUNCTION deny_snapshot_mutation() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION '% is append-only; create a new version', TG_TABLE_NAME; END $$;

CREATE FUNCTION validate_owned_content() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE v_building UUID; v_org UUID; v_actor UUID;
BEGIN
 IF TG_TABLE_NAME='buildings' THEN
   v_building:=NEW.id; v_org:=NEW.organization_id; v_actor:=NEW.created_by;
 ELSE
   v_building:=NEW.building_id; v_org:=NEW.organization_id;
   v_actor:=COALESCE((to_jsonb(NEW)->>'uploaded_by')::uuid,(to_jsonb(NEW)->>'created_by')::uuid);
 END IF;
 PERFORM lock_content(v_building);
 IF TG_OP='INSERT' THEN PERFORM assert_org_user(v_actor,v_org);
 ELSIF (to_jsonb(NEW)->>'organization_id',to_jsonb(NEW)->>'building_id',to_jsonb(NEW)->>'created_by',to_jsonb(NEW)->>'uploaded_by')
 IS DISTINCT FROM (to_jsonb(OLD)->>'organization_id',to_jsonb(OLD)->>'building_id',to_jsonb(OLD)->>'created_by',to_jsonb(OLD)->>'uploaded_by')
 THEN RAISE EXCEPTION 'Ownership and creator are immutable'; END IF;
 IF TG_TABLE_NAME<>'buildings' AND TG_OP='INSERT' AND NOT EXISTS(SELECT 1 FROM buildings WHERE id=v_building AND is_active AND deleted_at IS NULL)
 THEN RAISE EXCEPTION 'Active building required'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER owned_building BEFORE INSERT OR UPDATE ON buildings FOR EACH ROW EXECUTE FUNCTION validate_owned_content();
CREATE TRIGGER owned_revision BEFORE INSERT OR UPDATE ON revisions FOR EACH ROW EXECUTE FUNCTION validate_owned_content();
CREATE TRIGGER owned_scenario BEFORE INSERT ON scenarios FOR EACH ROW EXECUTE FUNCTION validate_owned_content();

CREATE FUNCTION validate_source() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF r.status<>'Draft' THEN RAISE EXCEPTION 'Source can be registered/quarantined only while revision Draft'; END IF;
 IF TG_OP='INSERT' THEN PERFORM assert_org_user(NEW.uploaded_by,r.organization_id);
 ELSIF (to_jsonb(NEW)-ARRAY['quarantine_status','quarantine_note']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['quarantine_status','quarantine_note'])
 OR OLD.quarantine_status<>'Pending' THEN RAISE EXCEPTION 'Source identity immutable; only pending quarantine can be resolved'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER source_write BEFORE INSERT OR UPDATE ON source_documents FOR EACH ROW EXECUTE FUNCTION validate_source();
CREATE TRIGGER source_no_delete BEFORE DELETE ON source_documents FOR EACH ROW EXECUTE FUNCTION deny_snapshot_mutation();

CREATE FUNCTION enforce_revision_status_transition() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 PERFORM lock_content(NEW.building_id);
 IF TG_OP='INSERT' THEN
   IF NEW.status<>'Draft' THEN RAISE EXCEPTION 'Revision starts Draft'; END IF;
   RETURN NEW;
 END IF;
 IF (NEW.id,NEW.building_id,NEW.organization_id,NEW.uploaded_by,NEW.version_label,NEW.primary_type,NEW.created_at)
 IS DISTINCT FROM (OLD.id,OLD.building_id,OLD.organization_id,OLD.uploaded_by,OLD.version_label,OLD.primary_type,OLD.created_at)
 THEN RAISE EXCEPTION 'Revision identity immutable'; END IF;
 IF NEW.status=OLD.status THEN RETURN NEW; END IF;
 IF NOT ((OLD.status='Draft' AND NEW.status='Uploaded')
 OR (OLD.status IN ('Uploaded','NeedsFix','Failed') AND NEW.status='Processing')
 OR (OLD.status='Processing' AND NEW.status IN ('ReadyForScenario','NeedsFix','Failed'))
 OR (OLD.status='ReadyForScenario' AND NEW.status='ConfirmedForTraining')
 OR (OLD.status IN ('NeedsFix','Failed','ReadyForScenario','ConfirmedForTraining','Rejected') AND NEW.status='Superseded'))
 THEN RAISE EXCEPTION 'Invalid revision transition % -> %',OLD.status,NEW.status; END IF;
 IF NEW.status='Uploaded' AND NOT EXISTS(SELECT 1 FROM source_documents WHERE revision_id=NEW.id AND quarantine_status='Accepted')
 THEN RAISE EXCEPTION 'Uploaded requires accepted IFC source'; END IF;
 IF NEW.status='ReadyForScenario' AND NOT EXISTS(SELECT 1 FROM validation_runs v JOIN processing_jobs j ON j.id=v.job_id
 WHERE v.revision_id=NEW.id AND v.kind='Geometry' AND v.outcome='Passed' AND j.status='Succeeded')
 THEN RAISE EXCEPTION 'ReadyForScenario requires successful geometry QA/job'; END IF;
 IF NEW.status='ConfirmedForTraining' AND NOT EXISTS(SELECT 1 FROM revision_reviews WHERE revision_id=NEW.id AND action='ConfirmForTraining')
 THEN RAISE EXCEPTION 'Confirmation review required'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER revision_state BEFORE INSERT OR UPDATE ON revisions FOR EACH ROW EXECUTE FUNCTION enforce_revision_status_transition();

CREATE FUNCTION validate_processing_job() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF TG_OP='INSERT' THEN
   IF NEW.status<>'Queued' THEN RAISE EXCEPTION 'Job starts Queued'; END IF;
   IF NOT EXISTS(SELECT 1 FROM source_documents WHERE id=NEW.source_document_id AND quarantine_status='Accepted')
   THEN RAISE EXCEPTION 'Job requires accepted source'; END IF;
   IF NEW.kind='Geometry' AND r.status NOT IN ('Uploaded','Processing','NeedsFix','Failed')
   THEN RAISE EXCEPTION 'Geometry job not allowed for immutable ready revision'; END IF;
   IF NEW.kind='Scenario' AND (r.status NOT IN ('ReadyForScenario','ConfirmedForTraining') OR NOT EXISTS
   (SELECT 1 FROM scenario_versions WHERE id=NEW.scenario_version_id AND revision_id=r.id))
   THEN RAISE EXCEPTION 'Scenario job requires matching ready revision/version'; END IF;
 ELSE
   IF (to_jsonb(NEW)-ARRAY['status','lease_owner','heartbeat_at','started_at','finished_at','error_message'])
   IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['status','lease_owner','heartbeat_at','started_at','finished_at','error_message'])
   THEN RAISE EXCEPTION 'Job identity immutable'; END IF;
   IF OLD.status IN ('Succeeded','Failed') THEN RAISE EXCEPTION 'Terminal job immutable; retry with new job'; END IF;
   IF NOT (NEW.status=OLD.status OR (OLD.status='Queued' AND NEW.status='Running') OR (OLD.status='Running' AND NEW.status IN ('Succeeded','Failed')))
   THEN RAISE EXCEPTION 'Invalid job transition'; END IF;
 END IF;
 IF NEW.status='Running' AND (NEW.started_at IS NULL OR NULLIF(btrim(NEW.lease_owner),'') IS NULL OR NEW.heartbeat_at IS NULL)
 THEN RAISE EXCEPTION 'Running job requires start and lease heartbeat'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER processing_job_write BEFORE INSERT OR UPDATE ON processing_jobs FOR EACH ROW EXECUTE FUNCTION validate_processing_job();

CREATE FUNCTION validate_revision_child() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions; j processing_jobs;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF TG_TABLE_NAME='revision_floors' THEN
   IF r.status<>'Processing' OR EXISTS(SELECT 1 FROM validation_runs WHERE revision_id=r.id AND kind='Geometry' AND outcome='Passed')
   THEN RAISE EXCEPTION 'Floor snapshot requires Processing revision before geometry QA passes'; END IF;
 END IF;
 IF TG_TABLE_NAME='annotation_sets' THEN
   IF r.status NOT IN ('Processing','ReadyForScenario','ConfirmedForTraining') THEN RAISE EXCEPTION 'Invalid annotation revision'; END IF;
   PERFORM assert_org_user(NEW.created_by,r.organization_id);
 END IF;
 IF TG_TABLE_NAME='revision_artifacts' THEN
   SELECT * INTO STRICT j FROM processing_jobs WHERE id=NEW.job_id;
   IF j.status<>'Running' OR r.status NOT IN ('Processing','ReadyForScenario','ConfirmedForTraining')
   THEN RAISE EXCEPTION 'Artifact requires a running job and usable revision'; END IF;
   IF EXISTS(SELECT 1 FROM validation_runs WHERE job_id=j.id) THEN RAISE EXCEPTION 'Job artifacts frozen after validation'; END IF;
   IF (NEW.artifact_type='CandidatePackage')<>(j.kind='Scenario') THEN RAISE EXCEPTION 'Artifact/job kind mismatch'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER floor_insert BEFORE INSERT ON revision_floors FOR EACH ROW EXECUTE FUNCTION validate_revision_child();
CREATE TRIGGER annotation_insert BEFORE INSERT ON annotation_sets FOR EACH ROW EXECUTE FUNCTION validate_revision_child();
CREATE TRIGGER artifact_insert BEFORE INSERT ON revision_artifacts FOR EACH ROW EXECUTE FUNCTION validate_revision_child();

CREATE FUNCTION validate_scenario_version_write() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF r.status NOT IN ('ReadyForScenario','ConfirmedForTraining') THEN RAISE EXCEPTION 'Scenario requires ready geometry'; END IF;
 PERFORM assert_org_user(NEW.created_by,NEW.organization_id);
 IF NEW.schema_version<>'1.0' OR NEW.npc_config<>'{}'::jsonb
 OR jsonb_typeof(NEW.spawn_config->'nodeId') IS DISTINCT FROM 'string'
 OR NULLIF(btrim(NEW.spawn_config->>'nodeId'),'') IS NULL
 OR jsonb_typeof(NEW.goal_config->'exitIds') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'Invalid Phase 1 scenario schema/spawn/goals/NPC'; END IF;
 IF jsonb_array_length(NEW.goal_config->'exitIds')=0 OR EXISTS(SELECT 1 FROM jsonb_array_elements(NEW.goal_config->'exitIds') x
 WHERE jsonb_typeof(x)<>'string' OR btrim(x#>>'{}')='') THEN RAISE EXCEPTION 'At least one nonempty exit ID required'; END IF;
 IF NOT (NEW.routing_config ?& ARRAY['hazardWeight','portalWeight'])
 OR jsonb_typeof(NEW.routing_config->'hazardWeight') IS DISTINCT FROM 'number'
 OR jsonb_typeof(NEW.routing_config->'portalWeight') IS DISTINCT FROM 'number'
 THEN RAISE EXCEPTION 'Numeric routing weights required'; END IF;
 IF (NEW.routing_config->>'hazardWeight')::numeric<0 OR (NEW.routing_config->>'portalWeight')::numeric<0
 THEN RAISE EXCEPTION 'Routing weights must be nonnegative'; END IF;
 -- Server fingerprint of PostgreSQL canonical JSONB; do not reimplement this serialization in Flutter/Unity.
 NEW.scenario_hash:=encode(digest(convert_to((to_jsonb(NEW)-ARRAY['id','scenario_hash','created_at','created_by'])::text,'UTF8'),'sha256'),'hex');
 RETURN NEW;
END $$;
CREATE TRIGGER scenario_insert BEFORE INSERT ON scenario_versions FOR EACH ROW EXECUTE FUNCTION validate_scenario_version_write();

CREATE FUNCTION validate_qa_run() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE j processing_jobs; s scenario_versions; r revisions;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT j FROM processing_jobs WHERE id=NEW.job_id;
 IF j.status<>'Running' OR j.kind<>NEW.kind THEN RAISE EXCEPTION 'QA requires matching running job'; END IF;
 IF NEW.kind='Geometry' AND NEW.outcome='Passed' THEN
   IF NOT EXISTS(SELECT 1 FROM revision_floors WHERE revision_id=NEW.revision_id)
   OR (SELECT count(DISTINCT artifact_type) FROM revision_artifacts WHERE job_id=j.id AND artifact_type IN ('Geometry','Graph','HazardGrid'))<>3
   THEN RAISE EXCEPTION 'Geometry QA needs floors, geometry, graph and hazard artifacts'; END IF;
 END IF;
 IF NEW.kind='Scenario' THEN
   SELECT * INTO STRICT s FROM scenario_versions WHERE id=NEW.scenario_version_id;
   IF s.revision_id<>NEW.revision_id OR s.id<>j.scenario_version_id OR s.scenario_hash<>NEW.scenario_hash
   OR NOT EXISTS(SELECT 1 FROM revision_artifacts WHERE id=NEW.candidate_artifact_id AND job_id=j.id AND artifact_type='CandidatePackage')
   THEN RAISE EXCEPTION 'QA scenario/hash/candidate mismatch'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER qa_insert BEFORE INSERT ON validation_runs FOR EACH ROW EXECUTE FUNCTION validate_qa_run();

CREATE FUNCTION validate_revision_review_action() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions; v validation_runs;
BEGIN
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id FOR UPDATE;
 PERFORM assert_org_user(NEW.reviewed_by,r.organization_id);
 SELECT * INTO STRICT v FROM validation_runs WHERE id=NEW.validation_run_id;
 IF r.status NOT IN ('ReadyForScenario','ConfirmedForTraining') OR v.kind<>'Scenario'
 OR v.revision_id<>r.id OR v.scenario_version_id<>NEW.scenario_version_id
 OR v.annotation_set_id IS DISTINCT FROM NEW.annotation_set_id
 OR NOT EXISTS(SELECT 1 FROM processing_jobs WHERE id=v.job_id AND status='Succeeded')
 THEN RAISE EXCEPTION 'Review requires exact scenario QA and successful job'; END IF;
 IF NEW.action='ConfirmForTraining' AND v.outcome<>'Passed' THEN RAISE EXCEPTION 'Cannot confirm failed QA'; END IF;
 IF NEW.action='Rejected' AND EXISTS(SELECT 1 FROM revision_reviews WHERE scenario_version_id=NEW.scenario_version_id AND action='ConfirmForTraining')
 THEN RAISE EXCEPTION 'Confirmed scenario immutable; revoke its release if necessary'; END IF;
 RETURN NEW;
END $$;
CREATE FUNCTION apply_revision_review_action() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.action='ConfirmForTraining' THEN UPDATE revisions SET status='ConfirmedForTraining'
 WHERE id=NEW.revision_id AND status='ReadyForScenario'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER review_validate BEFORE INSERT ON revision_reviews FOR EACH ROW EXECUTE FUNCTION validate_revision_review_action();
CREATE TRIGGER review_apply AFTER INSERT ON revision_reviews FOR EACH ROW EXECUTE FUNCTION apply_revision_review_action();
CREATE FUNCTION confirm_revision_for_training(p_revision UUID,p_scenario UUID,p_actor UUID,p_validation UUID,p_message TEXT DEFAULT NULL)
RETURNS UUID LANGUAGE plpgsql AS $$
DECLARE v_id UUID;
BEGIN
 INSERT INTO revision_reviews(revision_id,scenario_version_id,reviewed_by,validation_run_id,annotation_set_id,action,review_message)
 SELECT p_revision,p_scenario,p_actor,v.id,v.annotation_set_id,'ConfirmForTraining',p_message FROM validation_runs v WHERE v.id=p_validation
 RETURNING id INTO v_id;
 IF v_id IS NULL THEN RAISE EXCEPTION 'Validation run missing'; END IF;
 RETURN v_id;
END $$;

CREATE FUNCTION validate_release_write() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r revisions;
BEGIN
 PERFORM lock_content(NEW.building_id);
 SELECT * INTO STRICT r FROM revisions WHERE id=NEW.revision_id;
 IF TG_OP='INSERT' THEN
   IF NEW.status<>'Built' OR r.status<>'ConfirmedForTraining' OR NOT EXISTS(SELECT 1 FROM revision_reviews
   WHERE id=NEW.confirmation_review_id AND revision_id=NEW.revision_id AND scenario_version_id=NEW.scenario_version_id AND action='ConfirmForTraining')
   THEN RAISE EXCEPTION 'Built release requires exact confirmed revision/scenario/review'; END IF;
 ELSE
   IF (to_jsonb(NEW)-ARRAY['status','published_by','published_at','revoked_by','revoked_at','revoked_reason','updated_at'])
   IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['status','published_by','published_at','revoked_by','revoked_at','revoked_reason','updated_at'])
   THEN RAISE EXCEPTION 'Release snapshot immutable'; END IF;
   IF OLD.published_at IS NOT NULL AND (NEW.published_at,NEW.published_by) IS DISTINCT FROM (OLD.published_at,OLD.published_by)
   THEN RAISE EXCEPTION 'Publish provenance immutable'; END IF;
   IF OLD.status='Revoked' AND (to_jsonb(NEW)-'updated_at') IS DISTINCT FROM (to_jsonb(OLD)-'updated_at')
   THEN RAISE EXCEPTION 'Revoked release terminal'; END IF;
   IF NEW.status<>OLD.status AND NOT ((OLD.status='Built' AND NEW.status IN ('Published','Revoked'))
   OR (OLD.status='Published' AND NEW.status IN ('Superseded','Revoked')) OR (OLD.status='Superseded' AND NEW.status='Revoked'))
   THEN RAISE EXCEPTION 'Invalid release transition'; END IF;
   IF NEW.status<>'Published' AND EXISTS(SELECT 1 FROM release_qr_codes WHERE release_id=NEW.id AND is_active)
   THEN RAISE EXCEPTION 'Deactivate every QR before closing release'; END IF;
   IF NEW.status='Published' AND OLD.status<>'Published' THEN
     PERFORM assert_org_user(NEW.published_by,NEW.organization_id);
     IF r.status<>'ConfirmedForTraining' OR NOT EXISTS(SELECT 1 FROM release_packages WHERE release_id=NEW.id)
     OR NOT EXISTS(SELECT 1 FROM trainings WHERE release_id=NEW.id AND status='Active')
     OR NOT EXISTS(SELECT 1 FROM buildings WHERE id=NEW.building_id AND is_active AND deleted_at IS NULL)
     THEN RAISE EXCEPTION 'Publish requires usable revision/building, package and Active Training'; END IF;
   END IF;
 END IF;
 IF NEW.status='Revoked' AND (TG_OP='INSERT' OR OLD.status<>'Revoked') THEN PERFORM assert_org_user(NEW.revoked_by,NEW.organization_id); END IF;
 IF NEW.safety_thresholds IS DISTINCT FROM (SELECT safety_thresholds FROM scenario_versions WHERE id=NEW.scenario_version_id)
 THEN RAISE EXCEPTION 'Release thresholds must match confirmed scenario'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER release_write BEFORE INSERT OR UPDATE ON releases FOR EACH ROW EXECUTE FUNCTION validate_release_write();
CREATE FUNCTION validate_package() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r releases; a revision_artifacts; v validation_runs;
BEGIN
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
 SELECT v1.* INTO STRICT v FROM validation_runs v1 JOIN revision_reviews rv ON rv.validation_run_id=v1.id WHERE rv.id=r.confirmation_review_id;
 SELECT * INTO STRICT a FROM revision_artifacts WHERE id=NEW.candidate_artifact_id;
 IF r.status<>'Built' OR a.id<>v.candidate_artifact_id OR a.sha256_hash<>NEW.checksum_sha256 OR a.size_bytes<>NEW.package_size_bytes
 OR a.object_key<>NEW.package_url THEN RAISE EXCEPTION 'Package must match exact confirmed candidate artifact'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER package_insert BEFORE INSERT ON release_packages FOR EACH ROW EXECUTE FUNCTION validate_package();

CREATE FUNCTION validate_training_write() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r releases;
BEGIN
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
 IF TG_OP='INSERT' THEN
   PERFORM assert_org_user(NEW.created_by,NEW.organization_id);
   IF NEW.status NOT IN ('Draft','Active') THEN RAISE EXCEPTION 'Training starts Draft or Active'; END IF;
 ELSE
   IF (NEW.id,NEW.release_id,NEW.scenario_version_id,NEW.organization_id,NEW.created_by,NEW.created_at)
   IS DISTINCT FROM (OLD.id,OLD.release_id,OLD.scenario_version_id,OLD.organization_id,OLD.created_by,OLD.created_at)
   THEN RAISE EXCEPTION 'Training binding immutable'; END IF;
   IF NEW.status<>OLD.status AND NOT ((OLD.status='Draft' AND NEW.status IN ('Active','Archived'))
   OR (OLD.status='Active' AND NEW.status='Closed') OR (OLD.status='Closed' AND NEW.status='Archived'))
   THEN RAISE EXCEPTION 'Invalid Training transition'; END IF;
   IF (NEW.mode,NEW.allowed_modes,NEW.max_attempts,NEW.start_date,NEW.end_date)
   IS DISTINCT FROM (OLD.mode,OLD.allowed_modes,OLD.max_attempts,OLD.start_date,OLD.end_date)
   AND (EXISTS(SELECT 1 FROM release_qr_codes WHERE training_id=OLD.id) OR EXISTS(SELECT 1 FROM sessions WHERE training_id=OLD.id))
   THEN RAISE EXCEPTION 'Training policy frozen once any QR/session exists'; END IF;
   IF NEW.status<>'Active' AND EXISTS(SELECT 1 FROM release_qr_codes WHERE training_id=NEW.id AND is_active)
   THEN RAISE EXCEPTION 'Deactivate QR before closing Training'; END IF;
 END IF;
 IF NEW.status IN ('Draft','Active') AND r.status NOT IN ('Built','Published') THEN RAISE EXCEPTION 'Usable release required to activate Training'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER training_write BEFORE INSERT OR UPDATE ON trainings FOR EACH ROW EXECUTE FUNCTION validate_training_write();
CREATE FUNCTION validate_release_qr_code() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r releases; t trainings;
BEGIN
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id; PERFORM lock_content(r.building_id);
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
 SELECT * INTO STRICT t FROM trainings WHERE id=NEW.training_id;
 IF TG_OP='UPDATE' THEN
   IF (to_jsonb(NEW)-'is_active') IS DISTINCT FROM (to_jsonb(OLD)-'is_active') OR (NOT OLD.is_active AND NEW.is_active)
   THEN RAISE EXCEPTION 'QR immutable; rotate with a new token/row'; END IF;
 ELSE PERFORM assert_org_user(NEW.created_by,NEW.organization_id); END IF;
 IF NEW.revision_floor_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM revision_floors WHERE id=NEW.revision_floor_id AND revision_id=r.revision_id)
 THEN RAISE EXCEPTION 'QR floor must belong to pinned revision'; END IF;
 IF NEW.is_active AND (r.status<>'Published' OR t.status<>'Active' OR (NEW.expires_at IS NOT NULL AND NEW.expires_at<=clock_timestamp()))
 THEN RAISE EXCEPTION 'Active QR requires Published release, Active Training and future expiry'; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER qr_write BEFORE INSERT OR UPDATE ON release_qr_codes FOR EACH ROW EXECUTE FUNCTION validate_release_qr_code();

CREATE FUNCTION validate_training_session() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE r releases; t trainings; q release_qr_codes; res session_results;
BEGIN
 SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
 IF TG_OP='INSERT' THEN
   PERFORM lock_content(r.building_id);
   SELECT * INTO STRICT r FROM releases WHERE id=NEW.release_id;
   SELECT * INTO STRICT t FROM trainings WHERE id=NEW.training_id;
   SELECT * INTO STRICT q FROM release_qr_codes WHERE id=NEW.qr_code_id;
   IF NEW.status<>'Created' OR NEW.launched_at IS NOT NULL OR NEW.terminal_reason IS NOT NULL
   THEN RAISE EXCEPTION 'Session starts Created'; END IF;
   NEW.started_at:=clock_timestamp();
   IF NOT EXISTS(SELECT 1 FROM users WHERE id=NEW.trainee_user_id AND role='Trainee' AND is_active AND deleted_at IS NULL)
   OR NOT EXISTS(SELECT 1 FROM organizations WHERE id=NEW.organization_id AND is_active AND deleted_at IS NULL)
   OR NOT EXISTS(SELECT 1 FROM buildings WHERE id=r.building_id AND is_active AND deleted_at IS NULL)
   THEN RAISE EXCEPTION 'Active Trainee and content organization/building required'; END IF;
   IF r.status<>'Published' OR t.status<>'Active' OR NOT q.is_active OR (q.expires_at IS NOT NULL AND q.expires_at<=clock_timestamp())
   OR (t.start_date IS NOT NULL AND t.start_date>clock_timestamp()) OR (t.end_date IS NOT NULL AND t.end_date<=clock_timestamp())
   OR NOT NEW.mode::text=ANY(t.allowed_modes) THEN RAISE EXCEPTION 'QR/Training/release/mode/window unavailable'; END IF;
   IF NEW.mode='Assessment' AND t.max_attempts IS NOT NULL AND (SELECT count(*) FROM sessions WHERE training_id=t.id
   AND trainee_user_id=NEW.trainee_user_id AND mode='Assessment')>=t.max_attempts THEN RAISE EXCEPTION 'Assessment attempts exhausted'; END IF;
   IF NEW.scenario_hash IS DISTINCT FROM (SELECT scenario_hash FROM scenario_versions WHERE id=NEW.scenario_version_id)
   OR NEW.release_hash IS DISTINCT FROM (SELECT checksum_sha256 FROM release_packages WHERE release_id=r.id)
   THEN RAISE EXCEPTION 'Session content hash mismatch'; END IF;
 ELSE
   IF (to_jsonb(NEW)-ARRAY['status','launched_at','ended_at','terminal_reason','content_status_at_completion'])
   IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['status','launched_at','ended_at','terminal_reason','content_status_at_completion'])
   THEN RAISE EXCEPTION 'Session bindings, start and hashes immutable'; END IF;
   IF OLD.status NOT IN ('Created','Launching','Running') THEN RAISE EXCEPTION 'Terminal session immutable'; END IF;
   IF NEW.status=OLD.status THEN
     IF NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION 'Lifecycle metadata changes require transition'; END IF;
     RETURN NEW;
   END IF;
   IF NOT ((OLD.status='Created' AND NEW.status IN ('Launching','Aborted','Abandoned'))
   OR (OLD.status='Launching' AND NEW.status IN ('Running','Crashed','Aborted','Abandoned'))
   OR (OLD.status='Running' AND NEW.status IN ('Completed','CompletedWithSupersededRelease','ScenarioUnsurvivable','Crashed','Aborted','Abandoned')))
   THEN RAISE EXCEPTION 'Invalid Session transition'; END IF;
   IF NEW.status='Running' THEN NEW.launched_at:=clock_timestamp();
   ELSIF NEW.launched_at IS DISTINCT FROM OLD.launched_at THEN RAISE EXCEPTION 'Launch time immutable'; END IF;
   IF NEW.status NOT IN ('Created','Launching','Running') THEN
     NEW.ended_at:=clock_timestamp(); NEW.content_status_at_completion:=r.status;
     IF NEW.status IN ('Completed','CompletedWithSupersededRelease','ScenarioUnsurvivable') THEN
       SELECT * INTO res FROM session_results WHERE session_id=NEW.id;
       IF NOT FOUND THEN RAISE EXCEPTION 'Result required before completing Session'; END IF;
       IF (NEW.status='ScenarioUnsurvivable')=res.reached_exit THEN RAISE EXCEPTION 'Outcome/result mismatch'; END IF;
       IF NEW.status='CompletedWithSupersededRelease' AND r.status<>'Superseded' THEN RAISE EXCEPTION 'Release is not superseded'; END IF;
       IF NEW.status='Completed' AND r.status='Superseded' THEN RAISE EXCEPTION 'Use CompletedWithSupersededRelease'; END IF;
     END IF;
   END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER session_write BEFORE INSERT OR UPDATE ON sessions FOR EACH ROW EXECUTE FUNCTION validate_training_session();

CREATE FUNCTION validate_session_child() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE s sessions; n BIGINT; last_seq BIGINT;
BEGIN
 SELECT * INTO STRICT s FROM sessions WHERE id=NEW.session_id FOR UPDATE;
 IF s.status<>'Running' THEN RAISE EXCEPTION 'Events/results/checkpoints require Running Session'; END IF;
 IF TG_TABLE_NAME='session_events' THEN
   IF NEW.schema_version<>'1.0' THEN RAISE EXCEPTION 'Unsupported event schema'; END IF;
 END IF;
 IF TG_TABLE_NAME='session_checkpoints' THEN
   IF NEW.release_hash<>s.release_hash OR NEW.scenario_hash<>s.scenario_hash THEN RAISE EXCEPTION 'Checkpoint content hash mismatch'; END IF;
 END IF;
 IF TG_TABLE_NAME='session_results' THEN
   SELECT count(*),COALESCE(max(sequence_number),0) INTO n,last_seq FROM session_events WHERE session_id=s.id;
   IF n<>NEW.last_event_sequence OR last_seq<>NEW.last_event_sequence THEN RAISE EXCEPTION 'Completion requires contiguous event sequence 1..last'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER event_insert BEFORE INSERT ON session_events FOR EACH ROW EXECUTE FUNCTION validate_session_child();
CREATE TRIGGER result_insert BEFORE INSERT ON session_results FOR EACH ROW EXECUTE FUNCTION validate_session_child();
CREATE TRIGGER checkpoint_insert BEFORE INSERT ON session_checkpoints FOR EACH ROW EXECUTE FUNCTION validate_session_child();
CREATE FUNCTION verify_result_committed_terminal() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM sessions WHERE id=NEW.session_id AND status IN ('Completed','CompletedWithSupersededRelease','ScenarioUnsurvivable'))
 THEN RAISE EXCEPTION 'Result and terminal Session must commit together'; END IF;
 RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER result_terminal AFTER INSERT ON session_results DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION verify_result_committed_terminal();

CREATE FUNCTION start_training_session(p_actor UUID,p_qr UUID,p_device UUID,p_mode session_mode_enum,p_start_key UUID,
 p_app TEXT,p_unity TEXT,p_protocol TEXT DEFAULT '1.0') RETURNS UUID LANGUAGE plpgsql AS $$
DECLARE old_session sessions; q release_qr_codes; r releases; s_id UUID;
BEGIN
 PERFORM pg_advisory_xact_lock(hashtextextended(p_actor::text||':'||p_start_key::text,602));
 SELECT * INTO old_session FROM sessions WHERE trainee_user_id=p_actor AND start_key=p_start_key;
 IF FOUND THEN
   IF (old_session.qr_code_id,old_session.device_id,old_session.mode,old_session.app_version,old_session.unity_version,old_session.protocol_version)
   IS DISTINCT FROM (p_qr,p_device,p_mode,p_app,p_unity,p_protocol) THEN RAISE EXCEPTION 'Start key payload conflict'; END IF;
   RETURN old_session.id;
 END IF;
 IF p_protocol<>'1.0' THEN RAISE EXCEPTION 'Unsupported launch protocol'; END IF;
 SELECT * INTO STRICT q FROM release_qr_codes WHERE id=p_qr;
 SELECT * INTO STRICT r FROM releases WHERE id=q.release_id;
 PERFORM lock_content(r.building_id);
 INSERT INTO sessions(training_id,release_id,scenario_version_id,organization_id,trainee_user_id,device_id,qr_code_id,start_key,
 app_version,unity_version,protocol_version,scenario_hash,release_hash,mode)
 SELECT q.training_id,r.id,r.scenario_version_id,r.organization_id,p_actor,p_device,p_qr,p_start_key,p_app,p_unity,p_protocol,
 v.scenario_hash,p.checksum_sha256,p_mode FROM scenario_versions v JOIN release_packages p ON p.release_id=r.id WHERE v.id=r.scenario_version_id
 RETURNING id INTO s_id;
 IF s_id IS NULL THEN RAISE EXCEPTION 'Package unavailable'; END IF;
 RETURN s_id;
END $$;
CREATE FUNCTION append_session_event(p_actor UUID,p_session UUID,p_sequence BIGINT,p_event_id UUID,p_type TEXT,p_data JSONB,
 p_elapsed BIGINT,p_recorded TIMESTAMPTZ,p_schema TEXT DEFAULT '1.0') RETURNS UUID LANGUAGE plpgsql AS $$
DECLARE s sessions; e session_events; new_id UUID;
BEGIN
 SELECT * INTO STRICT s FROM sessions WHERE id=p_session FOR UPDATE;
 IF s.trainee_user_id IS DISTINCT FROM p_actor THEN RAISE EXCEPTION 'Session owner mismatch'; END IF;
 SELECT * INTO e FROM session_events WHERE session_id=p_session AND (sequence_number=p_sequence OR client_event_id=p_event_id);
 IF FOUND THEN
   IF (e.sequence_number,e.client_event_id,e.event_type,e.event_data,e.elapsed_ms,e.recorded_at,e.schema_version)
   IS DISTINCT FROM (p_sequence,p_event_id,p_type,p_data,p_elapsed,p_recorded,p_schema) THEN RAISE EXCEPTION 'Event identity/payload conflict'; END IF;
   RETURN e.id;
 END IF;
 INSERT INTO session_events(session_id,sequence_number,client_event_id,event_type,event_data,elapsed_ms,recorded_at,schema_version)
 VALUES(p_session,p_sequence,p_event_id,p_type,p_data,p_elapsed,p_recorded,p_schema) RETURNING id INTO new_id;
 RETURN new_id;
END $$;
CREATE FUNCTION complete_training_session(p_actor UUID,p_session UUID,p_key UUID,p_last BIGINT,p_result JSONB,p_outcome session_status_enum)
RETURNS UUID LANGUAGE plpgsql AS $$
DECLARE s sessions; old_result session_results; new_id UUID; release_status release_status_enum; final_status session_status_enum;
BEGIN
 IF p_outcome IS NULL OR p_outcome NOT IN ('Completed','ScenarioUnsurvivable') THEN RAISE EXCEPTION 'Unsupported completion outcome'; END IF;
 SELECT * INTO STRICT s FROM sessions WHERE id=p_session FOR UPDATE;
 IF s.trainee_user_id IS DISTINCT FROM p_actor THEN RAISE EXCEPTION 'Session owner mismatch'; END IF;
 SELECT * INTO old_result FROM session_results WHERE session_id=s.id;
 IF FOUND THEN
   IF old_result.completion_key IS DISTINCT FROM p_key OR old_result.last_event_sequence IS DISTINCT FROM p_last
   OR old_result.submission_payload IS DISTINCT FROM p_result
   OR (p_outcome='ScenarioUnsurvivable')<>(s.status='ScenarioUnsurvivable') THEN RAISE EXCEPTION 'Completion key/payload conflict'; END IF;
   RETURN old_result.id;
 END IF;
 IF p_result IS NULL OR jsonb_typeof(p_result)<>'object' OR NOT(p_result ?& ARRAY['score','timeTakenSeconds','reachedExit','rubricVersion'])
 THEN RAISE EXCEPTION 'Completion requires score, duration, reachedExit and rubricVersion'; END IF;
 INSERT INTO session_results(session_id,completion_key,last_event_sequence,submission_payload,rubric_version,score,time_taken_seconds,
 wrong_exits,hazard_exposure_score,total_distance_meters,reached_exit,exit_point_id,path_traveled)
 VALUES(s.id,p_key,p_last,p_result,p_result->>'rubricVersion',(p_result->>'score')::numeric,(p_result->>'timeTakenSeconds')::int,
 COALESCE((p_result->>'wrongExits')::int,0),COALESCE((p_result->>'hazardExposureScore')::numeric,0),
 COALESCE((p_result->>'totalDistanceMeters')::numeric,0),(p_result->>'reachedExit')::boolean,p_result->>'exitPointId',
 COALESCE(p_result->'pathTraveled','[]')) RETURNING id INTO new_id;
 SELECT status INTO STRICT release_status FROM releases WHERE id=s.release_id;
 final_status:=CASE WHEN p_outcome='Completed' AND release_status='Superseded' THEN 'CompletedWithSupersededRelease'::session_status_enum ELSE p_outcome END;
 UPDATE sessions SET status=final_status WHERE id=s.id;
 RETURN new_id;
END $$;

CREATE FUNCTION guard_identity_change() RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
 IF TG_TABLE_NAME='user_devices' THEN
   IF (NEW.id,NEW.user_id,NEW.device_uuid,NEW.created_at) IS DISTINCT FROM (OLD.id,OLD.user_id,OLD.device_uuid,OLD.created_at)
   THEN RAISE EXCEPTION 'Device registration identity immutable'; END IF;
 ELSIF TG_TABLE_NAME='users' THEN
   IF (NEW.id,NEW.role,NEW.organization_id) IS DISTINCT FROM (OLD.id,OLD.role,OLD.organization_id)
   THEN RAISE EXCEPTION 'Account role/org immutable; provision a separate account'; END IF;
 ELSIF TG_TABLE_NAME='building_floors' THEN
   IF (NEW.id,NEW.building_id,NEW.organization_id,NEW.floor_number) IS DISTINCT FROM (OLD.id,OLD.building_id,OLD.organization_id,OLD.floor_number)
   THEN RAISE EXCEPTION 'Floor identity immutable; snapshot changes in new revision'; END IF;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER user_identity BEFORE UPDATE ON users FOR EACH ROW EXECUTE FUNCTION guard_identity_change();
CREATE TRIGGER device_identity BEFORE UPDATE ON user_devices FOR EACH ROW EXECUTE FUNCTION guard_identity_change();
CREATE TRIGGER floor_identity BEFORE UPDATE ON building_floors FOR EACH ROW EXECUTE FUNCTION guard_identity_change();
CREATE FUNCTION record_core_audit() RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE v JSONB:=to_jsonb(NEW); previous JSONB; v_org UUID; v_actor UUID; v_action audit_action_enum;
BEGIN
 IF TG_OP='UPDATE' THEN previous:=to_jsonb(OLD); END IF;
 v_org:=(v->>'organization_id')::uuid;
 IF v_org IS NULL AND v ? 'revision_id' THEN SELECT organization_id INTO v_org FROM revisions WHERE id=(v->>'revision_id')::uuid; END IF;
 IF v_org IS NULL AND v ? 'session_id' THEN SELECT organization_id INTO v_org FROM sessions WHERE id=(v->>'session_id')::uuid; END IF;
 v_actor:=COALESCE((v->>'reviewed_by')::uuid,(v->>'uploaded_by')::uuid,(v->>'created_by')::uuid,(v->>'trainee_user_id')::uuid);
 v_action:=CASE WHEN TG_OP='INSERT' THEN 'Create'::audit_action_enum ELSE 'Update'::audit_action_enum END;
 IF TG_TABLE_NAME='source_documents' AND TG_OP='INSERT' THEN v_action:='Upload'; END IF;
 IF TG_TABLE_NAME='revision_reviews' THEN v_action:=CASE WHEN NEW.action='ConfirmForTraining' THEN 'ConfirmForTraining'::audit_action_enum ELSE 'Reject'::audit_action_enum END; END IF;
 IF TG_TABLE_NAME='releases' AND TG_OP='UPDATE' THEN
   IF NEW.status='Published' AND OLD.status<>'Published' THEN v_action:='Publish'; v_actor:=NEW.published_by; END IF;
   IF NEW.status='Revoked' AND OLD.status<>'Revoked' THEN v_action:='Revoke'; v_actor:=NEW.revoked_by; END IF;
 END IF;
 -- For UPDATE creator is not necessarily the actor. API must add its authenticated command audit/correlation.
 IF TG_OP='UPDATE' AND v_action='Update' THEN v_actor:=NULL; END IF;
 INSERT INTO audit_logs(user_id,organization_id,actor_type,action,target_entity,target_id,old_values,new_values)
 VALUES(v_actor,v_org,CASE WHEN v_actor IS NULL THEN 'System' ELSE 'User' END,v_action,TG_TABLE_NAME,(v->>'id')::uuid,
 CASE WHEN previous IS NULL THEN NULL ELSE jsonb_build_object('status',previous->'status','isActive',previous->'is_active') END,
 jsonb_build_object('status',v->'status','isActive',v->'is_active'));
 RETURN NEW;
END $$;
DO $$ DECLARE t TEXT; BEGIN
 FOREACH t IN ARRAY ARRAY['annotation_sets','revision_floors','scenarios','scenario_versions','revision_artifacts','validation_runs',
 'revision_issues','revision_reviews','release_packages','revision_processing_logs','session_events','session_results','session_checkpoints','debrief_artifacts','audit_logs']
 LOOP EXECUTE format('CREATE TRIGGER immutable_snapshot BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION deny_snapshot_mutation()',t); END LOOP;
 FOREACH t IN ARRAY ARRAY['revisions','processing_jobs','releases','trainings','release_qr_codes','sessions']
 LOOP EXECUTE format('CREATE TRIGGER retained_history BEFORE DELETE ON %I FOR EACH ROW EXECUTE FUNCTION deny_snapshot_mutation()',t); END LOOP;
 FOREACH t IN ARRAY ARRAY['organizations','users','buildings','building_locations','building_floors','building_contacts','revisions','releases','trainings']
 LOOP EXECUTE format('CREATE TRIGGER set_updated_at BEFORE UPDATE ON %I FOR EACH ROW EXECUTE FUNCTION update_updated_at_column()',t); END LOOP;
 FOREACH t IN ARRAY ARRAY['buildings','revisions','source_documents','scenario_versions','revision_reviews','releases','release_qr_codes','sessions','session_results']
 LOOP EXECUTE format('CREATE TRIGGER core_audit AFTER INSERT OR UPDATE ON %I FOR EACH ROW EXECUTE FUNCTION record_core_audit()',t); END LOOP;
END $$;
-- App-only permissions applied at end of baseline.
-- App-only function permissions applied at end of baseline.
-- Deployment must grant narrowly to backend/worker logins. No RLS claim: tenant
-- SELECT authorization remains in API; see database security/command contract.


-- SOURCE: 30_phase2.sql
-- Phase 2 retained from v5; not a claim of production integration readiness.
CREATE TABLE service_packages (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(), -- Gói dịch vụ thương mại
    code            VARCHAR(50) UNIQUE NOT NULL,                -- Mã ổn định dùng trong quotation
    name            VARCHAR(255) NOT NULL,
    description     TEXT,
    unit_price      DECIMAL(14, 2) NOT NULL,
    currency        VARCHAR(3) NOT NULL DEFAULT 'VND',
    duration_months INT,
    features        JSONB NOT NULL DEFAULT '{}',
    is_active       BOOLEAN NOT NULL DEFAULT true,
    created_by      UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL, -- PlatformAdmin tạo/quản lý
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_service_package_price CHECK (unit_price >= 0),
    CONSTRAINT check_service_package_currency CHECK (currency ~ '^[A-Z]{3}$'),
    CONSTRAINT check_service_package_duration CHECK (duration_months IS NULL OR duration_months > 0)
);

CREATE TABLE quotations (
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id    UUID REFERENCES organizations(id) ON DELETE RESTRICT NOT NULL,
    service_package_id UUID REFERENCES service_packages(id) ON DELETE RESTRICT NOT NULL,
    requested_by       UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL, -- OrganizationUser yêu cầu
    issued_by          UUID REFERENCES users(id) ON DELETE RESTRICT,          -- PlatformAdmin phát hành
    quotation_number   VARCHAR(50) UNIQUE NOT NULL,
    status             quotation_status_enum NOT NULL DEFAULT 'Draft',
    quantity           INT NOT NULL DEFAULT 1,
    unit_price         DECIMAL(14, 2) NOT NULL,
    subtotal_amount    DECIMAL(14, 2) NOT NULL,
    tax_amount         DECIMAL(14, 2) NOT NULL DEFAULT 0,
    discount_amount    DECIMAL(14, 2) NOT NULL DEFAULT 0,
    total_amount       DECIMAL(14, 2) NOT NULL,
    currency           VARCHAR(3) NOT NULL DEFAULT 'VND',
    valid_until        TIMESTAMPTZ NOT NULL,
    issued_at          TIMESTAMPTZ,
    accepted_at        TIMESTAMPTZ,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_quotation_quantity CHECK (quantity > 0),
    CONSTRAINT check_quotation_amounts CHECK (
        unit_price >= 0
        AND subtotal_amount = unit_price * quantity
        AND tax_amount >= 0
        AND discount_amount >= 0
        AND total_amount = subtotal_amount + tax_amount - discount_amount
        AND total_amount >= 0
    ),
    CONSTRAINT check_quotation_currency CHECK (currency ~ '^[A-Z]{3}$'),
    CONSTRAINT check_quotation_issued CHECK (
        status = 'Draft' OR (issued_by IS NOT NULL AND issued_at IS NOT NULL)
    )
);

CREATE TABLE payos_payment_requests (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    quotation_id        UUID REFERENCES quotations(id) ON DELETE RESTRICT NOT NULL,
    organization_id     UUID REFERENCES organizations(id) ON DELETE RESTRICT NOT NULL,
    requested_by        UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL,
    order_code          BIGINT UNIQUE NOT NULL,                     -- PayOS orderCode expected
    expected_amount     DECIMAL(14, 2) NOT NULL,                    -- Amount expected from quotation
    expected_currency   VARCHAR(3) NOT NULL DEFAULT 'VND',          -- Currency expected from quotation
    checkout_url        TEXT NOT NULL,
    return_url          TEXT NOT NULL,                              -- Chỉ điều hướng UI; không xác nhận thanh toán
    cancel_url          TEXT NOT NULL,
    status              payment_request_status_enum NOT NULL DEFAULT 'Pending',
    paid_transaction_id UUID UNIQUE,                               -- FK ghép được thêm sau payment_transactions
    expires_at          TIMESTAMPTZ,
    paid_at             TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_payos_order_code CHECK (order_code > 0),
    CONSTRAINT check_payos_expected_amount CHECK (expected_amount > 0),
    CONSTRAINT check_payos_expected_currency CHECK (expected_currency ~ '^[A-Z]{3}$'),
    CONSTRAINT check_payos_paid_fields CHECK (
        (status = 'Paid' AND paid_transaction_id IS NOT NULL AND paid_at IS NOT NULL)
        OR (status != 'Paid' AND paid_transaction_id IS NULL AND paid_at IS NULL)
    )
);

CREATE TABLE payment_transactions (
    id                         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_request_id         UUID REFERENCES payos_payment_requests(id) ON DELETE RESTRICT NOT NULL,
    webhook_event_id           VARCHAR(255) UNIQUE NOT NULL, -- Khóa idempotency cho mỗi webhook PayOS
    provider_transaction_id    VARCHAR(255) UNIQUE,
    received_order_code        BIGINT NOT NULL,
    received_amount            DECIMAL(14, 2) NOT NULL,
    received_currency          VARCHAR(3) NOT NULL,
    signature_verified         BOOLEAN NOT NULL DEFAULT false,
    status                     payment_transaction_status_enum NOT NULL DEFAULT 'Received',
    raw_payload                JSONB NOT NULL,
    rejection_reason           TEXT,
    received_at                TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    signature_verified_at      TIMESTAMPTZ,
    processed_at               TIMESTAMPTZ,
    UNIQUE (id, payment_request_id),
    CONSTRAINT check_payment_transaction_amount CHECK (received_amount > 0),
    CONSTRAINT check_payment_transaction_currency CHECK (received_currency ~ '^[A-Z]{3}$'),
    CONSTRAINT check_payment_transaction_event_id CHECK (NULLIF(BTRIM(webhook_event_id), '') IS NOT NULL),
    CONSTRAINT check_payment_transaction_state_fields CHECK (
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
    )
);

ALTER TABLE payos_payment_requests
    ADD CONSTRAINT fk_payos_paid_transaction_for_request
    FOREIGN KEY (paid_transaction_id, id)
    REFERENCES payment_transactions(id, payment_request_id) ON DELETE RESTRICT;

CREATE TABLE invoice_metadata (
    id                     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_transaction_id UUID REFERENCES payment_transactions(id) ON DELETE RESTRICT UNIQUE NOT NULL,
    quotation_id           UUID REFERENCES quotations(id) ON DELETE RESTRICT NOT NULL,
    organization_id        UUID REFERENCES organizations(id) ON DELETE RESTRICT NOT NULL,
    invoice_number         VARCHAR(100) UNIQUE,
    legal_name             TEXT NOT NULL,
    tax_code               VARCHAR(50),
    billing_address        TEXT,
    subtotal_amount        DECIMAL(14, 2) NOT NULL,
    tax_amount             DECIMAL(14, 2) NOT NULL DEFAULT 0,
    total_amount           DECIMAL(14, 2) NOT NULL,
    currency               VARCHAR(3) NOT NULL DEFAULT 'VND',
    issued_at              TIMESTAMPTZ,
    invoice_url            TEXT,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_invoice_amounts CHECK (
        subtotal_amount >= 0
        AND tax_amount >= 0
        AND total_amount = subtotal_amount + tax_amount
    ),
    CONSTRAINT check_invoice_currency CHECK (currency ~ '^[A-Z]{3}$')
);

CREATE TABLE feedback (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    submitted_by    UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL,
    organization_id UUID REFERENCES organizations(id) ON DELETE SET NULL,
    session_id      UUID REFERENCES sessions(id) ON DELETE SET NULL,
    category        VARCHAR(100) NOT NULL,
    rating          INT,
    message         TEXT NOT NULL,
    status          feedback_status_enum NOT NULL DEFAULT 'Submitted',
    reviewed_by     UUID REFERENCES users(id) ON DELETE RESTRICT,
    reviewed_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_feedback_rating CHECK (rating IS NULL OR rating BETWEEN 1 AND 5),
    CONSTRAINT check_feedback_message CHECK (NULLIF(BTRIM(message), '') IS NOT NULL)
);

CREATE TABLE support_tickets (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticket_number   VARCHAR(50) UNIQUE NOT NULL,
    created_by      UUID REFERENCES users(id) ON DELETE RESTRICT NOT NULL,
    organization_id UUID REFERENCES organizations(id) ON DELETE SET NULL,
    session_id      UUID REFERENCES sessions(id) ON DELETE SET NULL,
    feedback_id     UUID REFERENCES feedback(id) ON DELETE SET NULL,
    assigned_to     UUID REFERENCES users(id) ON DELETE RESTRICT,
    subject         VARCHAR(255) NOT NULL,
    description     TEXT NOT NULL,
    priority        support_priority_enum NOT NULL DEFAULT 'Normal',
    status          support_ticket_status_enum NOT NULL DEFAULT 'Open',
    resolved_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT check_support_subject CHECK (NULLIF(BTRIM(subject), '') IS NOT NULL),
    CONSTRAINT check_support_description CHECK (NULLIF(BTRIM(description), '') IS NOT NULL),
    CONSTRAINT check_support_resolution CHECK (status != 'Resolved' OR resolved_at IS NOT NULL)
);

CREATE INDEX idx_service_packages_created_by ON service_packages(created_by);
CREATE INDEX idx_service_packages_active ON service_packages(code) WHERE is_active;
CREATE INDEX idx_quotations_organization ON quotations(organization_id);
CREATE INDEX idx_quotations_service_package ON quotations(service_package_id);
CREATE INDEX idx_quotations_requested_by ON quotations(requested_by);
CREATE INDEX idx_quotations_issued_by ON quotations(issued_by);
CREATE INDEX idx_payos_payment_requests_quotation ON payos_payment_requests(quotation_id);
CREATE INDEX idx_payos_payment_requests_organization ON payos_payment_requests(organization_id);
CREATE INDEX idx_payos_payment_requests_requested_by ON payos_payment_requests(requested_by);
CREATE INDEX idx_payos_payment_requests_status ON payos_payment_requests(status, created_at);
CREATE INDEX idx_payment_transactions_request ON payment_transactions(payment_request_id);
CREATE INDEX idx_payment_transactions_status ON payment_transactions(status, received_at);
CREATE INDEX idx_invoice_metadata_quotation ON invoice_metadata(quotation_id);
CREATE INDEX idx_invoice_metadata_organization ON invoice_metadata(organization_id);
CREATE INDEX idx_feedback_submitted_by ON feedback(submitted_by);
CREATE INDEX idx_feedback_organization ON feedback(organization_id);
CREATE INDEX idx_feedback_session ON feedback(session_id);
CREATE INDEX idx_feedback_reviewed_by ON feedback(reviewed_by);
CREATE INDEX idx_support_tickets_created_by ON support_tickets(created_by);
CREATE INDEX idx_support_tickets_organization ON support_tickets(organization_id);
CREATE INDEX idx_support_tickets_session ON support_tickets(session_id);
CREATE INDEX idx_support_tickets_feedback ON support_tickets(feedback_id);
CREATE INDEX idx_support_tickets_assigned_to ON support_tickets(assigned_to);
CREATE INDEX idx_support_tickets_queue ON support_tickets(status, priority, created_at);

-- ==============================================================================
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
CREATE OR REPLACE FUNCTION validate_payos_paid_request()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND (
        OLD.order_code != NEW.order_code
        OR OLD.expected_amount != NEW.expected_amount
        OR OLD.expected_currency != NEW.expected_currency
    ) THEN
        RAISE EXCEPTION 'PayOS expected orderCode, amount and currency are immutable';
    END IF;

    IF TG_OP = 'UPDATE' AND OLD.status = 'Paid' AND (
        NEW.status != 'Paid'
        OR NEW.paid_transaction_id IS DISTINCT FROM OLD.paid_transaction_id
        OR NEW.paid_at IS DISTINCT FROM OLD.paid_at
    ) THEN
        RAISE EXCEPTION 'Paid payment truth and provenance are immutable';
    END IF;

    IF TG_OP = 'UPDATE' AND NEW.status = 'Paid' AND OLD.status != 'Paid'
       AND OLD.status != 'Pending' THEN
        RAISE EXCEPTION 'Only a Pending payment request can become Paid';
    END IF;

    IF TG_OP = 'UPDATE' AND NEW.status = 'Paid' AND OLD.status != 'Paid'
       AND CURRENT_USER != 'fet3d_payos_ledger_owner' THEN
        RAISE EXCEPTION 'Paid payment request must use trusted PayOS webhook function';
    END IF;

    IF NEW.status = 'Paid' AND NOT EXISTS (
        SELECT 1
        FROM public.payment_transactions pt
        WHERE pt.id = NEW.paid_transaction_id
          AND pt.payment_request_id = NEW.id
          AND pt.status = 'Applied'
          AND pt.signature_verified
          AND pt.received_order_code = NEW.order_code
          AND pt.received_amount = NEW.expected_amount
          AND pt.received_currency = NEW.expected_currency
    ) THEN
        RAISE EXCEPTION 'Paid requires an Applied verified webhook matching orderCode, amount and currency';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Runtime payment creation path. The backend calls PayOS outside any DB transaction,
-- then invokes this short function with the returned HTTPS checkout URL. Amount,
-- currency and organization are derived from the locked Accepted quotation; callers
-- cannot choose them or create a status other than Pending.
CREATE OR REPLACE FUNCTION create_pending_payos_payment_request(
    p_quotation_id UUID,
    p_requested_by UUID,
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

    INSERT INTO public.payos_payment_requests (
        quotation_id,
        organization_id,
        requested_by,
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

COMMENT ON FUNCTION create_pending_payos_payment_request(
    UUID, UUID, BIGINT, TEXT, TEXT, TEXT, TIMESTAMPTZ
) IS
'Creates only Pending PayOS requests from an Accepted quotation after the backend completes the external PayOS call; returnUrl remains navigation-only.';

-- TRUST BOUNDARY: before invoking this function, the backend adapter must use
-- the official PayOS SDK webhooks.verify(req.body), or the official equivalent
-- that canonicalizes webhook data by alphabetically sorting the data fields and
-- verifies the resulting signature. Do not verify against raw HTTP request bytes.
-- This SECURITY DEFINER function performs no cryptography; it records the trusted
-- adapter attestation, enforces idempotency and checks orderCode/amount/currency.
-- It accepts no caller-supplied signature flag. Only the dedicated webhook executor
-- may execute it, and that role has no direct DML on either payment table.
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

CREATE TRIGGER enforce_payment_transaction_state_before_write
BEFORE INSERT OR UPDATE OR DELETE
ON payment_transactions FOR EACH ROW EXECUTE FUNCTION enforce_payment_transaction_state();

CREATE TRIGGER validate_payos_paid_before_write
BEFORE INSERT OR UPDATE OF order_code, expected_amount, expected_currency,
    status, paid_transaction_id, paid_at
ON payos_payment_requests FOR EACH ROW EXECUTE FUNCTION validate_payos_paid_request();

CREATE TRIGGER set_ts_service_packages BEFORE UPDATE ON service_packages FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER set_ts_quotations BEFORE UPDATE ON quotations FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER set_ts_payos_payment_requests BEFORE UPDATE ON payos_payment_requests FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER set_ts_invoice_metadata BEFORE UPDATE ON invoice_metadata FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER set_ts_feedback BEFORE UPDATE ON feedback FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER set_ts_support_tickets BEFORE UPDATE ON support_tickets FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- SECTION 7: PAYOS LEDGER PRIVILEGE BOUNDARY
-- ==============================================================================
-- This bootstrap requires a migration role with CREATEROLE and ownership of the
-- objects above (normally a deployment superuser). Runtime login roles receive
-- neither table ownership nor inheritance from fet3d_payos_ledger_owner. The trusted
-- checkout adapter receives only fet3d_payos_request_executor; the independently
-- trusted webhook adapter receives only fet3d_payos_webhook_executor. A deployment
-- may grant both roles to one backend login only if it owns both adapter duties;
-- never grant either executor role to browser/mobile/user-facing logins.
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

-- Non-superuser migration owner needs membership and target-owner schema CREATE.
GRANT fet3d_payos_ledger_owner, fet3d_payos_request_executor, fet3d_payos_webhook_executor TO CURRENT_USER;
GRANT CREATE ON SCHEMA public TO fet3d_payos_ledger_owner;
ALTER TABLE payos_payment_requests OWNER TO fet3d_payos_ledger_owner;
ALTER TABLE payment_transactions OWNER TO fet3d_payos_ledger_owner;
ALTER FUNCTION create_pending_payos_payment_request(
    UUID, UUID, BIGINT, TEXT, TEXT, TEXT, TIMESTAMPTZ
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
    UUID, UUID, BIGINT, TEXT, TEXT, TEXT, TIMESTAMPTZ
) FROM PUBLIC;
REVOKE ALL PRIVILEGES ON FUNCTION apply_verified_payos_webhook(
    UUID, TEXT, TEXT, BIGINT, NUMERIC, TEXT, JSONB
) FROM PUBLIC;

GRANT USAGE ON SCHEMA public
TO fet3d_payos_request_executor, fet3d_payos_webhook_executor, fet3d_payos_ledger_owner;
GRANT SELECT (id, organization_id, status, total_amount, currency, valid_until)
ON TABLE quotations TO fet3d_payos_ledger_owner;
GRANT SELECT (id, organization_id, role, is_active, deleted_at)
ON TABLE users TO fet3d_payos_ledger_owner;
GRANT EXECUTE ON FUNCTION create_pending_payos_payment_request(
    UUID, UUID, BIGINT, TEXT, TEXT, TEXT, TIMESTAMPTZ
) TO fet3d_payos_request_executor;
GRANT EXECUTE ON FUNCTION apply_verified_payos_webhook(
    UUID, TEXT, TEXT, BIGINT, NUMERIC, TEXT, JSONB
) TO fet3d_payos_webhook_executor;


-- SOURCE: 001_auth_refresh_tokens.sql
-- Additive upgrade for an existing Fire3D v6 database. Run once as schema owner.
-- This does not re-run the v6 bootstrap or create ASP.NET Identity tables.

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


-- Trusted backend group, never grant to anon/authenticated/service_role.
CREATE ROLE fire3d_api NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
GRANT USAGE ON SCHEMA public TO fire3d_api;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM fet3d_payos_ledger_owner;
DO $permissions$
DECLARE n text; r text; f record;
BEGIN
 FOREACH n IN ARRAY ARRAY['organizations','users','user_devices','buildings','building_locations','building_floors','building_contacts','revisions','source_documents','annotation_sets','processing_jobs','revision_processing_logs','revision_floors','scenarios','scenario_versions','revision_artifacts','validation_runs','revision_issues','revision_reviews','releases','release_packages','trainings','release_qr_codes','sessions','session_events','session_results','session_checkpoints','debrief_artifacts','audit_logs','service_packages','quotations','payos_payment_requests','payment_transactions','invoice_metadata','feedback','support_tickets','auth_refresh_tokens'] LOOP
  EXECUTE format('REVOKE ALL ON TABLE public.%I FROM PUBLIC', n);
  FOREACH r IN ARRAY ARRAY['anon','authenticated','service_role'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
    EXECUTE format('REVOKE ALL ON TABLE public.%I FROM %I', n, r);
   END IF;
  END LOOP;
  EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', n);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure AS signature, p.prosecdef
  FROM pg_proc p JOIN pg_namespace ns ON ns.oid=p.pronamespace
  WHERE ns.nspname='public' AND p.proname=ANY(ARRAY['append_session_event','apply_revision_review_action','apply_verified_payos_webhook','assert_org_user','complete_training_session','confirm_revision_for_training','create_pending_payos_payment_request','deny_snapshot_mutation','enforce_payment_transaction_state','enforce_revision_status_transition','guard_identity_change','lock_content','record_core_audit','start_training_session','update_updated_at_column','validate_owned_content','validate_package','validate_payos_paid_request','validate_processing_job','validate_qa_run','validate_release_qr_code','validate_release_write','validate_revision_child','validate_revision_review_action','validate_scenario_version_write','validate_session_child','validate_source','validate_training_session','validate_training_write','verify_result_committed_terminal']) LOOP
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f.signature);
  FOREACH r IN ARRAY ARRAY['anon','authenticated','service_role'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) THEN
    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I', f.signature, r);
   END IF;
  END LOOP;
  IF NOT f.prosecdef THEN
   EXECUTE format('ALTER FUNCTION %s SET search_path = pg_catalog, public, extensions, pg_temp', f.signature);
  END IF;
 END LOOP;
END $permissions$;
-- Only the auth/admin module is granted to today's backend runtime.
GRANT SELECT, INSERT, UPDATE ON public.users, public.organizations, public.auth_refresh_tokens TO fire3d_api;
GRANT INSERT ON public.audit_logs TO fire3d_api;
CREATE POLICY fire3d_api_users ON public.users TO fire3d_api USING (true) WITH CHECK (true);
CREATE POLICY fire3d_api_organizations ON public.organizations TO fire3d_api USING (true) WITH CHECK (true);
CREATE POLICY fire3d_api_sessions ON public.auth_refresh_tokens TO fire3d_api USING (true) WITH CHECK (true);
CREATE POLICY fire3d_api_audit_insert ON public.audit_logs FOR INSERT TO fire3d_api WITH CHECK (true);
-- SECURITY DEFINER payment functions retain their narrow owner and column grants.
CREATE POLICY fire3d_ledger_user_read ON public.users FOR SELECT TO fet3d_payos_ledger_owner USING (true);
CREATE POLICY fire3d_ledger_quote_read ON public.quotations FOR SELECT TO fet3d_payos_ledger_owner USING (true);
-- No row-level tenant isolation claim: trusted BE must authorize every request.
