# Context — BE

## Hiện trạng đã kiểm tra

- [.NET 10 solution](../Fire3D/Fire3D.slnx) gồm API, Application, Domain và Infrastructure.
- [Program.cs](../Fire3D/Fire3D.API/Program.cs) đăng ký database, controllers, OpenAPI/Swagger cho development; không suy ra mọi nghiệp vụ đã có chỉ vì entities tồn tại.
- [Database DI](../Fire3D/Fire3D.API/Extensions/DependencyInjection.cs) dùng EF Core/Npgsql và yêu cầu `ConnectionStrings:DefaultConnection`.
- [Domain enums](../Fire3D/Fire3D.Domain/Enums/DatabaseEnums.cs) và mapping PostgreSQL cần khớp tên/nhãn SQL. DI dùng `NpgsqlNullNameTranslator` để giữ chữ hoa/thường.
- Persistence ở [DbContext](../Fire3D/Fire3D.Infrastructure/Persistence/Fire3DDbContext.cs). Không chạy migration hoặc đổi schema database ngoài phạm vi được yêu cầu.
- Không lưu connection string/credential vào context hoặc handoff. Không track .vs, bin, obj.

## Kiến trúc đích đã thống nhất — 2026-09-17

- Backend dùng C# + ASP.NET Core trên .NET; EF Core/Npgsql kết nối Supabase Database (managed PostgreSQL). Supabase Auth không được dùng.
- Web/Mobile xác thực bằng Firebase Authentication, gồm Google Sign-In. API xác minh Firebase ID token rồi lấy role, trạng thái và `organizationId` từ PostgreSQL; không tin role/tenant do client gửi hoặc custom claim đơn lẻ.
- Firebase/Google quản lý login credential. Schema đích lưu `firebase_uid`, không lưu password hash hay Google refresh token. Backend vẫn sở hữu launch grant, signed URL và authorization nghiệp vụ.
- FCM dùng cho push notification. Registration token là metadata installation có thể rotate/revoke, không phải token đăng nhập.
- Raw IFC, manifest và content package nằm trong AWS S3 private; backend cấp signed URL TTL ngắn. Không đưa AWS, Firebase Admin hoặc Supabase service-role secret vào client.
- Azure đã được chọn cho AI/RAG FastAPI service; compute cho BE, IFC/Blender worker và Unity Editor worker, cùng SKU/region/cost, vẫn phải spike và chốt riêng. Không suy ra toàn bộ hệ thống chạy Azure.
- Nginx là reverse proxy đã chốt trước API. Nginx tiếp nhận HTTP(S) và chuyển request vào .NET; backend vẫn là nơi xác minh identity, tenant, quota, scope và gọi AI nội bộ. Domain, TLS termination, upstream và vị trí chạy Nginx là việc triển khai còn lại.
- Backend là authority cho Building service entitlement: kỳ tháng, ngày hiệu lực/hết hạn, trial, publish/session gate và lịch sử price/terms snapshot. Một organization có nhiều Building với kỳ khác nhau; không suy ra auto-recurring PayOS.
- Payment flow mục tiêu là quotation/request → PayOS checkout → verified webhook → đối soát → payment record → grant/extend đúng Building. `returnUrl`/`cancelUrl` chỉ điều hướng; retry/webhook trùng không cấp quyền hoặc ghi tiền trùng.
- Backend sở hữu AI quota/usage: organization quota dùng chung giữa các Building, ledger theo organization/building/user/audience/request type, overage theo AI billing period riêng; Trainee daily quota riêng. Request idempotency và unit price/terms snapshot phải truy vết được.
- Backend lưu `ai_requests` bền vững với canonical input hash, status/result/citations/model và request scope. Tạo input đi qua contract `create_ai_request`; ghi result đi qua contract result/reconcile trước khi accounting settle. Reservation allocations có thể phân bổ nhiều grant organization; usage ledger chỉ giữ provenance/giá/consent, còn settlement items/adjustments giữ membership kỳ đã chốt. Không dùng `SKIP LOCKED` để kết luận hết quota và không để AI tính tiền.
- Quota grant phải phân biệt audience `organization` và `trainee`: Trainee daily grant gắn trực tiếp user, không yêu cầu organization. Usage liên kết grant, request idempotency, price snapshot và overage consent; settlement period có snapshot và provenance thanh toán riêng.
- QR là canonical Building QR → list published trainings → session preparation pin selected training/release/scenario → explicit online session start. Preparation không cấp quyền; `POST /api/training/sessions/{sessionId}/start` kiểm tra entitlement/QR/package/runtime và idempotency, sau đó launch grant pin identity bất biến. Backend phải allow continuation/sync after network loss và heartbeat analytics.
- OrganizationUser có thể chạy playtest draft/version riêng; start kiểm tra Trial còn quota thử hoặc Building entitlement Active. Backend cấp playtest grant/session type đúng tenant, không tạo learner analytics, không dùng QR public và không coi Trial là quyền publish/session Trainee.
- Dashboard định nghĩa riêng unique trainees, total plays, active sessions heartbeat, completion/duration và building usage; OrganizationUser chỉ thấy tenant của mình.
- `total plays` chỉ tính learner sessions đã bắt đầu; playtest/session chuẩn bị bị loại. `active sessions` là ước tính từ heartbeat trong cửa sổ cấu hình và phải dùng cùng định nghĩa trong API/requirements/dashboard.
- Publish chỉ được phép khi package có checksum/manifest/runtime tương thích, ValidationRun Passed và không còn Error/Critical issue mở; payment Applied có provisioning key ổn định và record reconcile nếu cấp entitlement lỗi.
- Quota reserve/chốt/hoàn phải là transaction PostgreSQL ngắn với row lock hoặc conditional update; không giữ transaction khi chờ AI/PayOS/S3/worker. Transactional outbox, worker lease/attempt và reconcile xử lý eventual consistency giữa service.
- Runtime start dùng actor-bound backend function, server-owned compatibility catalog và semver `major.minor.patch`; `SECURITY DEFINER` start functions không được execute bởi `PUBLIC`. Người khác không thể launch bằng session ID hoặc update trực tiếp trạng thái chuẩn bị.
- Transaction boundary là transaction PostgreSQL ngắn cho reserve/provision/idempotency; LLM, PayOS, S3 và worker nằm ngoài transaction. Timeout AI chuyển `NeedsReconcile`; outbox/lease/attempt fencing và reconcile xử lý retry, không ghi đè kết quả stale. Heartbeat lưu server-received time, event dùng stable ID + sequence/schema để deduplicate.
- `origin/main` đã từng có triển khai password/JWT/password reset. Hướng này xung đột với quyết định Firebase mới và chưa được migrate bởi task tài liệu này; không xóa migration hoặc sửa auth code ngầm. Task triển khai sau phải lập migration/rollback và kiểm thử token verification, account mapping, revoke/disable và dữ liệu hiện có.

## Chạy và kiểm tra

Từ gốc BE, với .NET 10 SDK/phụ thuộc sẵn sàng:
- `dotnet build Fire3D/Fire3D.slnx`: kiểm tra biên dịch, không thay thế integration test.
- `dotnet run --project Fire3D/Fire3D.API/Fire3D.API.csproj`: chạy API khi task cần và đã có cấu hình database phù hợp.
- Chưa thấy test project trong solution đã kiểm tra; không dùng kết quả `dotnet test` không chạy test nào để kết luận nghiệp vụ đạt.
- Với thay đổi database/enum, cần kiểm tra có mục tiêu trên database test được phép dùng; không tự kết nối production.
- Ghi bằng chứng build/test của từng task trong handoff local hoặc PR; context này không phải báo cáo kiểm thử.

## Thiết kế liên quan

Khi có Docs bên cạnh, đối chiếu `fire_evacuation_schema.sql`, `fire_evacuation_erd.md`, requirements và workflows theo task. Docs giữ ba vai trò PlatformAdmin, OrganizationUser, Trainee; readiness nội bộ không phải phê duyệt PCCC. Nếu clone độc lập thiếu tài liệu bắt buộc, báo thiếu thay vì đoán schema hoặc tự clone.

## Invariant SQL/contract bổ sung — 2026-09-18

- Runtime compatibility của publish, Trainee start và playtest dùng một helper fail-closed; thiếu minimum runtime, protocol, manifest schema hoặc capability metadata bị từ chối. Start replay yêu cầu idempotency key và runtime payload giống nhau.
- AI billing period đóng băng snapshot ở `Closed`, nhưng gắn quotation `AIUsage` tại `Closed → Invoiced` và payment `Applied` tại `Invoiced → Paid`; adjustment không sửa period cũ.
- Trigger AI request phải chạy `BEFORE INSERT OR UPDATE`; trigger INSERT kiểm tra role/tenant/policy, còn request đã tiếp nhận được reconcile dù user bị khóa. Worker claim/renew/accept phải fence current attempt và provenance.

## Contract hardening — 2026-09-18

- Technology/SQL là nguồn chính cho close AI period: period mới chỉ `Open`; close khóa period, snapshot item/tổng tiền trong một transaction; items sau close immutable, late usage dùng adjustment có tenant/idempotency riêng. Quotation `AIUsage` và payment `Applied` chỉ gắn đúng lifecycle.
- Reserve/settle dùng lock order `billing period nếu có → request → ledger/reservation → grant theo id tăng dần`; close period lấy giá/policy/consent lịch sử, không nhận đơn giá mới. Không giữ transaction khi gọi AI/PayOS/S3/worker. Terminal AI result/policy/input provenance không sửa lịch sử; user bị khóa sau accept vẫn reconcile qua contract result.
- Worker function contract có `Claimed`, `Busy`, `AlreadyCompleted`, `NotClaimable`, `Conflict`, `StaleAttempt`; requeue Failed là backend-authorized/idempotent qua outbox, Cancelled không tự chạy lại. Executor không có DML trực tiếp vào bảng bảo vệ.
- Release package/artifact đã publish hoặc session pin không được update/delete tại chỗ. Runtime catalog kiểm tra capability element và chọn version số học đủ minimum. Các invariant này chưa có database/concurrency execution test.
- Release package, session và playtest phải pin đồng nhất package hash, manifest hash, build target, artifact ID và validation-run ID; provenance artifact/manifest sai hoặc thiếu metadata bị chặn trước publish/start.
