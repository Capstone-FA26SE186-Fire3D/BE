# Test Building mutation

- OrganizationUser đăng nhập, POST `/api/buildings` với name/totalFloors và location/contact tùy chọn. Bỏ organizationId để dùng tenant DB của actor; gửi tổ chức khác phải 403.
- PlatformAdmin POST cùng form và organizationId trong body; thiếu ID phải 400 `VALIDATION_ERROR` + errors.organizationId. Query alias vẫn nhận; body/query mâu thuẫn phải 400.
- PUT `/api/buildings/{id}` bằng admin không cần organizationId query. Location/contact null giữ dữ liệu hiện có.
- Sai email, latitude=91, longitude=181 hoặc GeoJSON sai: 400 và errors theo trường. Không có mutation/audit.
- GET `/api/buildings` admin có thể lọc organizationId query; OrgUser không được chọn tenant khác. Trainee không dùng Building management CRUD.
- DELETE hai lần: đều thành công; chỉ lần chuyển inactive có audit. Đây là archive, không xóa lịch sử.

Bằng chứng tự động: `BuildingMutationContractTests` và bốn `AuthIntegrationTests.Building_*` dùng PostgreSQL disposable, migration thực tế. Restricted role chỉ INSERT audit, không SELECT; lỗi audit rollback Building và location. Race lifecycle kiểm account/organization sau khóa trước mutation. Chưa kiểm Supabase/deploy; không cần migration riêng cho task CRUD này.
