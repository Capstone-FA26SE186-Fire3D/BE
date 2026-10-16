# Test Building mutation

- OrganizationUser đăng nhập, POST `/api/buildings` với name/totalFloors và location/contact tùy chọn. Bỏ organizationId để dùng tenant DB của actor; gửi tổ chức khác phải 403.
- PlatformAdmin POST cùng form và organizationId trong body; thiếu ID phải 400 `VALIDATION_ERROR` + errors.organizationId. Query alias vẫn nhận; body/query mâu thuẫn phải 400.
- PUT `/api/buildings/{id}` bằng admin không cần organizationId query. Location/contact null giữ dữ liệu hiện có.
- Sai email, latitude=91, longitude=181 hoặc GeoJSON sai: 400 và errors theo trường. Không có mutation/audit.
- GET `/api/buildings` admin có thể lọc organizationId query; OrgUser không được chọn tenant khác. Trainee không dùng Building management CRUD.
- DELETE hai lần: đều thành công; chỉ lần chuyển inactive có audit. Đây là archive, không xóa lịch sử.
- POST thiếu `Idempotency-Key`: 400 `IDEMPOTENCY_KEY_REQUIRED`. Gửi lại cùng key + body: 201 cùng `id`, DB chỉ có 1 Building/1 audit. Cùng key, body khác: 409 `IDEMPOTENCY_KEY_CONFLICT`. Tài khoản khác dùng lại key: tạo Building riêng.
- GET detail lấy header `ETag`. PUT/DELETE thiếu `If-Match`: 428; ETag cũ (sau khi người khác đã PUT): 412 kèm `current` và ETag mới, DB không đổi. DELETE lặp lại với ETag cũ sau khi đã archive: 200, không thêm audit.

Bằng chứng tự động: `BuildingMutationContractTests` và bốn `AuthIntegrationTests.Building_*` dùng PostgreSQL disposable, migration thực tế. Restricted role chỉ INSERT audit, không SELECT; lỗi audit rollback Building và location. Race lifecycle kiểm account/organization sau khóa trước mutation. Idempotency/ETag: `AuthIntegrationTests.Building_create_is_idempotent_and_updates_need_a_current_etag` (cả chế độ runtime-login `fire3d_api`). Cần migration `20261014137000_AddBuildingCommandReceipts` (bảng v7 `application_command_receipts`).
