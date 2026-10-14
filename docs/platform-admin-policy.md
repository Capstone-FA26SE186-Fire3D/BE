# Quyền đặc biệt của PlatformAdmin

Quyết định phạm vi sản phẩm ngày 2026-09-15: PlatformAdmin được thao tác thay tổ chức, bao gồm dữ liệu nghiệp vụ, và xem thông tin cá nhân phục vụ quản trị. Cần team review tài liệu này cùng API contract trước khi triển khai từng module.

## Ma trận quyền và tiến độ

| Phạm vi | Quyền đã thống nhất | Hiện trạng thực thi |
|---|---|---|
| Tổ chức | Quản trị xuyên tổ chức | Có tạo, danh sách, chi tiết, đổi trạng thái |
| Tài khoản | Tạo, xem thông tin cá nhân, đổi trạng thái | Đã có; chỉ PlatformAdmin được gọi API quản trị |
| Tòa nhà / revision / IFC | Thực hiện nghiệp vụ thay tổ chức | Đã có: tạo/liệt kê Building với `organizationId` (body), đọc chi tiết, revision, IFC upload/process/QA, readiness (`/api/revisions/{id}/readiness`) |
| Kịch bản / phiên bản / release | Thực hiện nghiệp vụ thay tổ chức | Đã có: scenario, draft (`/api/scenarios/{id}/drafts`), version, review queue/duyệt (`/api/admin/scenario-reviews`), release build/list/publish/revoke (`/api/releases`) |
| Huấn luyện / phiên / kết quả | Quản trị, xem dữ liệu phục vụ hỗ trợ tổ chức | Đã có: trainings theo Building, analytics training toàn nền tảng và theo tổ chức (`/api/admin/organizations/{id}/analytics/training`), vận hành (`/api/admin/organizations/{id}/analytics/operations`) |
| Runtime catalog | Đăng ký phiên bản runtime và bật/tắt | Đã có: `GET/POST /api/admin/runtime-catalog`, `PATCH /api/admin/runtime-catalog/{id}` |
| Gói Trial của Building | Cấp, chỉnh lượt playtest, hủy | Đã có: `GET/POST /api/admin/buildings/{buildingId}/trials`, `PATCH .../trials/{entitlementId}` |

Quyền quản trị không tự tạo ra chức năng chưa có, không bỏ qua validation, trạng thái hay quy tắc nghiệp vụ. Việc sửa kết quả huấn luyện đã hoàn tất cần có use case hiệu chỉnh và lịch sử riêng được team chốt; quyết định này không mặc nhiên cho phép ghi đè kết quả gốc.

## Dữ liệu cá nhân

Hiện API quản lý trả ID, email, họ tên, vai trò, organizationId, trạng thái, lần đăng nhập gần nhất và thời điểm tạo/cập nhật. Không trả password/hash, refresh token hoặc thông tin bí mật xác thực. Các trường cá nhân/kết quả huấn luyện bổ sung phải được xác định theo DTO và mục đích của từng use case.

## Thao tác thay tổ chức

Admin sử dụng chính danh tính PlatformAdmin; không giả mạo user hoặc cấp token dưới danh tính người khác. Với API hiện tại, ID tài khoản/tổ chức xác định đối tượng thao tác. Module tương lai phải xác minh tài nguyên thuộc tổ chức đích, kiểm tra quyền ở server và áp dụng đầy đủ quy tắc nghiệp vụ. OrganizationUser tiếp tục bị giới hạn trong tổ chức của mình; quyền admin không làm rộng quyền của role khác.

Các thao tác tạo/đổi trạng thái hiện có audit ghi người thực hiện, tổ chức đích và correlation ID khi dữ liệu thay đổi. Yêu cầu cho module tương lai: thao tác thay tổ chức phải ghi actor admin, tổ chức/tài nguyên đích, hành động, thời điểm và lý do hỗ trợ; thông tin nhạy cảm không đưa vào log. Audit lượt đọc dữ liệu cá nhân/lý do hỗ trợ chưa được triển khai trong API hiện tại và cần task riêng nếu team chọn đưa vào tiêu chí nghiệm thu.

## Runtime catalog và Trial

Runtime catalog là danh sách phiên bản runtime (Unity) mà publish, playtest và phiên học được phép nhắm tới, kèm năng lực (`capabilities`, ví dụ `fire.basic`, `smoke.basic`) và contract tham số của từng năng lực.

- `POST /api/admin/runtime-catalog` nhận `runtimeVersion` (dạng `major.minor.patch`), `protocolVersion`, `manifestSchemaVersion`, `capabilities[]`, `capabilityContracts` (tùy chọn) và `isActive` (mặc định `true`). Server đọc contract bằng chính bộ đọc của editor, nên mỗi key contract phải là một capability đã khai báo. Dữ liệu sai trả 400 `VALIDATION_ERROR`, lỗi gom theo JSONPath. Mỗi version chỉ đăng ký được một lần (409 `RUNTIME_VERSION_EXISTS`).
- Đăng ký xong thì phiên bản không đổi được nữa. `PATCH /api/admin/runtime-catalog/{id}` với `{ "isActive": false }` chặn publish, playtest và phiên mới nhắm tới phiên bản đó. Thư viện vẫn kiểm tra capability theo các runtime đang active.
- `POST /api/admin/buildings/{buildingId}/trials` nhận `servicePackageId`, `days` (1–90) và `playtestUnits` (0–1000), rồi cấp Trial bắt đầu ngay. Trả 409 `BUILDING_SERVICE_ACTIVE` khi Building đang có kỳ trả phí, 409 `TRIAL_ALREADY_ACTIVE` khi còn Trial chưa hết hạn, 404 `SERVICE_PACKAGE_NOT_FOUND` khi gói không tồn tại. Trial chỉ mở playtest; Trial không thay quyền huấn luyện trả phí.
- `PATCH .../trials/{entitlementId}` nhận `playtestUnits` hoặc `cancel: true`. Số lượt không được thấp hơn số lượt đã dùng (409 `TRIAL_UNITS_BELOW_USED`). Ngày bắt đầu và kết thúc không sửa được vì trigger billing khóa chúng. Muốn gia hạn thì hủy Trial cũ rồi cấp Trial mới.
- Mọi thao tác ghi đều có audit: `RuntimeCatalog` (Create/Update) và `ServiceEntitlement` (Grant/Update, `operation` = `TrialCreate`/`TrialUpdate`).

## Nghiệm thu

- API quản trị hiện có: 401 khi thiếu/sai token, 403 với OrganizationUser/Trainee; kiểm tra actor ở Application và policy tại API.
- DTO quản lý không lộ thông tin xác thực. Thay đổi trạng thái thu hồi phiên theo docs/administration.md.
- Với mỗi module tương lai: thêm test admin thao tác đúng tổ chức đích, OrganizationUser bị từ chối truy cập chéo, không bỏ qua trạng thái/validation và audit xác định đúng admin.
- Swagger mô tả quyền; authorization thực tế nằm trong policy/handler, không nằm trong comment.