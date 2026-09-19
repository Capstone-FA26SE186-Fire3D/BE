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
- Web/Mobile dùng email/password do BE quản lý hoặc Google Sign-In qua Firebase. API xác minh password/Fire3D session hoặc Firebase ID token rồi lấy role, trạng thái và `organizationId` từ PostgreSQL; không tin role/tenant do client gửi hoặc custom claim đơn lẻ.
- Schema đích lưu password/refresh/reset token hash và `firebase_uid` nullable khi liên kết Google; không lưu plaintext password, Google refresh token hoặc FCM credential. Backend vẫn sở hữu launch grant, signed URL và authorization nghiệp vụ.
- BE target chịu trách nhiệm register trainee/organization, username global unique, profile ETag, organization name/address/phone, password change/reset/link Google và avatar S3 intent/complete/delete. Các endpoint này còn là implementation work, không coi context/SQL là migration đã chạy.
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
- `origin/main` đã từng có triển khai password/JWT/password reset. Đây là hiện trạng cần đối chiếu, không phải hướng bị thay bằng Firebase: local password/session vẫn giữ; Firebase chỉ bổ sung Google identity verification. Task triển khai sau phải lập migration/rollback và kiểm thử token verification, account mapping, revoke/disable và dữ liệu hiện có.

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

## Redis event/cache boundary — 2026-09-19

- .NET là điểm vào duy nhất cho FE/Mobile và sở hữu identity, tenant, entitlement, quota, billing, session/result và dashboard. Redis chỉ đứng sau backend; không dùng cache để cấp start/publish/revoke/quota/billing.
- Ghi nghiệp vụ và integration_outbox_events trong cùng transaction PostgreSQL. Dispatcher claim lease riêng rồi giao Redis Streams; consumer/worker ghi business effect và integration_event_consumptions bền vững trước ACK. Redis Pub/Sub chỉ thông báo tiến độ.
- Outbox event key/hash/schema/scope bất biến; replay và duplicate delivery phải idempotent. Redis mất hoặc mất ACK thì replay từ PostgreSQL. Worker vẫn claim processing attempt/lease qua contract hiện hành, không nhận lease worker từ stream message.
- Cache-aside chỉ cho catalog, package metadata, danh sách bài và dashboard, có tenant/version key, TTL và invalidation sau commit. FE/Mobile không nhận credential Redis và không kết nối trực tiếp.
- Backend enqueue event trong transaction nghiệp vụ bằng aggregate-derived tenant. `enqueue_integration_outbox_event` chỉ nhận allowlist `ProcessingJobRequested` + schema `1`; `enqueue_system_outbox_event` có allowlist hệ thống và executor riêng; `requeue_processing_job` là gate duy nhất tạo `ProcessingJobRequeue` và dùng owner requeue riêng. Dispatcher chỉ claim/renew/mark/fail; worker không có enqueue/helper DML. Consumer kiểm tra envelope/receipt trước tác động, commit business effect + receipt rồi mới ACK; cùng requeue key replay sau mọi trạng thái trả no-op, message sai metadata được giữ để chẩn đoán/replay. Owner requeue chỉ đọc tenant lineage và khóa outbox/job theo quyền tối thiểu; backend executor không nhận DML trực tiếp vào bảng bảo vệ.

## Learn blog boundary — 2026-09-19

- `.NET` là authority cho Learn CMS/public API: `PlatformAdmin` tạo Draft, có thể publish ngay hoặc lưu nháp, rồi hide/show/delete/restore qua gate; Learn không có endpoint approve riêng. Trainee chỉ bookmark và hỏi AI. `OrganizationUser` không quản trị Learn.
- PostgreSQL giữ `learn_posts`, immutable `learn_post_versions`, `learn_situations`, source links và `learn_bookmarks`. Public response/cache chỉ được lấy version Published của post Published; Hidden không public nhưng vẫn có thể RAG, còn Deleted phải bị loại dù Redis/index chưa invalidated.
- Backend validate content schema, ETag/revision, source Common/Approved và provider allowlist YouTube/Facebook/TikTok; iframe/script tùy ý bị chặn. Publish/hide/show audit trong transaction rồi phát `PlatformCacheInvalidation` qua outbox hiện có.
- API/EF/migration CMS, provider metadata check và RAG indexing còn là implementation work; không coi Learn prototype hoặc SQL thiết kế là đã triển khai.

## FET3D account and commercial billing boundary — 2026-09-19

- Trainee local phải gửi username ngay khi đăng ký; OrganizationUser gửi hồ sơ organization (tên, địa chỉ, điện thoại), username cá nhân tùy chọn. Google mới sau Firebase verification chọn Trainee hoặc OrganizationUser qua onboarding token ngắn hạn; account đã link không chọn lại role/tenant và không tự gia nhập organization có sẵn.
- Backend là authority cho username lowercase unique, profile ETag, password/session revoke, Google link, S3 avatar và organization profile. Game start không còn ProfileIncomplete username gate.
- Organization có nhiều Building. `quotation_building_items` là nguồn dòng dịch vụ theo Building; giá/discount/terms snapshot ở quotation/item, payment một lần có thể provision nhiều entitlement bằng key từng item. `enterprise_quote_requests` không tạo charge/entitlement trước quotation/payment.
- Background task tạo notification web/email trước 5 ngày theo entitlement/kỳ/kênh; Mailgun là kênh email, PostgreSQL/outbox giữ idempotency/retry. Discount không cộng dồn và không sửa lịch sử quotation.

## Regression corrections — 2026-09-19

- Target quotation header no longer contains `building_id`, `service_package_id` or `service_duration_months`; `quotation_building_items` is the only BuildingService line source. Any current EF/entity references are migration work, not a reason to reintroduce duplicate fields.
- Learn editorial orchestration uses a durable application-command receipt keyed by actor/operation/idempotency key and canonical input hash. The .NET service locks post → version → links, then writes state, audit and outbox atomically; SQL re-reads version status after acquiring locks.
- Password reset/change must run under the existing user advisory/database transaction, consume the reset token, revoke every refresh-token family and rely on `OnTokenValidated` family checks to reject old access JWTs. Current reset handler has not yet completed that target transaction/revocation behavior.
- Google onboarding completion must persist the created `user_id` and canonical input hash on the short-lived onboarding record so a retried completion returns the committed result instead of creating a second user or organization; it never stores a password or bearer token.
- Current SQL/Docs updates are design-only. Do not claim database execution, permission, concurrency, S3 or email recovery tests passed.
- Quotation target lifecycle records `accepted_at` once on `Issued → Accepted`; quotation lines cannot move between quotations after creation. The design grants the PayOS ledger owner only the row-lock privilege it needs, gives the processing owner attempt INSERT, and gives the backend executor the minimum auth/profile/Building write path; these privileges still require database execution tests.

## Recovery gate corrections — 2026-09-19

- Session preparation uses the dedicated executor with read access to its referenced identity, Building, training, package and validation rows. Session result/event writes go through `record_session_event` and `complete_training_session`; playtest completion goes through `complete_playtest_session`. Replay is keyed by event ID/sequence or result/completion key/hash and does not re-check entitlement or current user activity after start.
- Playtest creator activity is checked at preparation/start. A playtest already started may complete or sync after the creator is disabled or the entitlement expires.
- Processing workers register artifact/validation/issues through lease-bound `register_processing_output`; they do not receive direct provenance-table DML. Accounting uses `invoice_ai_billing_period` and `pay_ai_billing_period` for replay-safe settlement transitions.
- These are target SQL/contract changes only; permission, concurrency, recovery and runtime execution tests remain unrun.
- Quotation target lifecycle records `accepted_at` once on `Issued → Accepted`; quotation lines cannot move between quotations after creation. The design grants the PayOS ledger owner only the row-lock privilege it needs, gives the processing owner attempt INSERT, and gives the backend executor the minimum auth/profile/Building write path; these privileges still require database execution tests.
