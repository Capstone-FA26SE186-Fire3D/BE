CREATE EXTENSION IF NOT EXISTS pgcrypto;

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
