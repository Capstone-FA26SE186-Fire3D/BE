# Context — BE

## Contract sản phẩm hiện hành — Docs v7

Nguồn chuẩn: [v7 contract](../../Docs/schema_v7_contract.md) và [requirements](../../Docs/fire_evacuation_requirements.md). Các đoạn contract cũ bên dưới đã được hiệu chỉnh theo v7; đây là thiết kế đích, không phải bằng chứng implementation hoặc deployment đã đạt.

- Ba vai trò là PlatformAdmin, OrganizationUser, Trainee. Organization console quản lý Building/IFC/scenario/analytics/billing; Organization Library do Admin maintain (template/rubric mẫu/metadata thiết bị), tách khỏi Learn public. Template tùy chọn, capability runtime giới hạn thiết bị/hành động; kho riêng không tự chia sẻ.
- Mọi scenario/rubric version cần PlatformAdmin approval đúng content hash trước publish. Submit đóng băng nội dung; sửa tạo version/review mới. IFC QA/ConfirmForTraining là readiness kỹ thuật độc lập; publish cần cả readiness và approval.
- Building Private mặc định: verify participation code tạo grant gắn Trainee account và `access_revision`. Rotate/revoke code hoặc đổi visibility vô hiệu grant cũ. Public yêu cầu Trainee đăng nhập; QR chỉ resolve Building. List/package/prepare kiểm access; start online recheck access, approved/published, entitlement, runtime và learner seat.
- Seat tính theo user ID khác nhau tại Building/kỳ dịch vụ khi start lần đầu, nguyên tử và idempotent. Nhiều bài/lượt cùng kỳ chỉ một suất; login/list/prepare/playtest không tính. Upgrade giữ kỳ và số suất đã dùng; renewal mở kỳ mới. Session đã start pin entitlement/review/rubric, được tiếp tục/sync sau expiry/revoke, không có start mới offline.
- Gói từng Building là 6/12 tháng gồm game service, learner limit và AI quota. Quote có quota phải pin quota-policy kind, tenant/audience/unit và khoảng hiệu lực quota tuyệt đối nằm trong policy validity trước issue. New/Renewal/Upgrade không chồng kỳ đã cam kết; replay provisioning theo payment/line trả kết quả cũ kể cả hết hạn, không cấp quota trùng.
- Organization AI quota trả trước pooled từ Building grants và top-up đã thanh toán; Trainee quota ngày riêng. Không overage, consent overage, AI billing period hoặc nợ trả sau. Reserve/settle/release request vẫn tồn tại; đây không phải đối soát hóa đơn cuối kỳ.
- Learner RAG chỉ index `name`, `objectives`, `instructions` của scenario approved/published và mỗi retrieval kiểm quyền/service hiện tại. Không draft/rubric/đáp án/IFC private. Learn scope cần cả `learn_post_id` và published `learn_version_id`; Hidden có thể RAG với pointer hợp lệ, Deleted/Unpublished không. Mất quyền vẫn đọc giải thích kết quả của chính mình đã lưu và Common, không retrieval Building mới.
- Trainee tự chọn Learn/Guided Drill/Assessment, không prerequisite/sprint, retry không giới hạn, không certificate. AI bị chặn trong Assessment. Server lưu rubric hash, criterion results, score, outcome `Passed|NotPassed|Incomplete|NotAssessed` và lý do; session Completed không tự Passed. Giá/quota/expiry/rollover/upgrade và ngưỡng/trọng số cụ thể không được tự suy ra.

## Hiện trạng đã kiểm tra

- [.NET 10 solution](../Fire3D/Fire3D.slnx) gồm API, Application, Domain và Infrastructure.
- [Program.cs](../Fire3D/Fire3D.API/Program.cs) đăng ký database, controllers, OpenAPI/Swagger cho development; không suy ra mọi nghiệp vụ đã có chỉ vì entities tồn tại.
- [Database DI](../Fire3D/Fire3D.API/Extensions/DependencyInjection.cs) dùng EF Core/Npgsql và yêu cầu `ConnectionStrings:DefaultConnection`.
- [Domain enums](../Fire3D/Fire3D.Domain/Enums/DatabaseEnums.cs) và mapping PostgreSQL cần khớp tên/nhãn SQL. DI dùng `NpgsqlNullNameTranslator` để giữ chữ hoa/thường.
- Persistence ở [DbContext](../Fire3D/Fire3D.Infrastructure/Persistence/Fire3DDbContext.cs). Không chạy migration hoặc đổi schema database ngoài phạm vi được yêu cầu.
- Không lưu connection string/credential vào context hoặc handoff. Không track .vs, bin, obj.

## Luồng auth hiện có — 2026-10-03

Chi tiết request/response, lỗi và nguồn code tại [authentication.md](../docs/authentication.md), [API guide](../docs/api-docs.md) và [test Swagger](../docs/registration-payos-manual-test.md). Phần này mô tả source, không chứng minh deployment/provider đã nghiệm thu.

- Đăng ký cả Trainee/OrganizationUser: FE giữ form trong bộ nhớ → request-otp(email) → verify-otp(email,otp) nhận registrationToken → gửi toàn bộ form+proof tới register theo loại account →201 AccountResponse → login riêng để nhận JWT. Request/verify không tạo user/organization; register consume proof, identity/organization và audit atomic. Server gán role/tenant.
- Resend-verification gửi OTP sáu số cùng service với request-otp, không gửi link. Cooldown 60 giây, OTP 10 phút, proof 15 phút dùng một lần;5 request/email/giờ, 20/IP/giờ. Email đã có kể cả inactive/deleted trả409 EMAIL_EXISTS/errors.email, không enqueue. `/verify-email` giữ cho pending link legacy; forgot/reset-password là luồng khác.
- Trainee username lowercase unique 3–30 ký tự; OrganizationUser route nhận tên/địa chỉ/phone tổ chức, hiện không nhận username cá nhân. Password mới 6–128 ký tự, không chỉ whitespace, không trim; confirm khớp. DOB yyyy-MM-dd; emailVerifiedAt tiếp tục timestamp. Không tạo user pending mới trước OTP, không tự xóa user cũ từ flow này.
- Organization phone canonical unique giữa mọi tổ chức, kể cả inactive/soft-deleted; legacy NULL hợp lệ. Giữ normalization hiện có, không suy 0…/+84…. Email/Google/PATCH chỉ map constraint organizations_phone_normalized_key thành409 ORGANIZATION_PHONE_EXISTS với field error đúng DTO; conflict rollback proof/session/audit hoặc profile revision. Migration additive có masked preflight, không tự sửa dữ liệu; source/test không chứng minh index Supabase đã áp. [Test tay](../docs/organization-phone-manual-test.md).
- Personal phone optional, canonical unique giữa mọi user kể cả inactive/soft-deleted, nhiều NULL hợp lệ; không unique chéo organizations. Register/Google/PATCH map đúng users_phone_normalized_key thành409 PHONE_NUMBER_EXISTS/errors.phoneNumber; giữ proof khi conflict và semantics profile omitted/null/ETag. [Contract/preflight](../docs/personal-phone-uniqueness.md).
- Login xác thực password/rehash ngoài transaction; dưới khóa lifecycle rồi user, đọc lại và so sánh email/hash snapshot cùng trạng thái identity trước khi ghi login/family/refresh hash/audit atomic, giữ reset fence. Response login/refresh gồm accessToken,refreshToken,user, không có expiresAt; TTL lấy Jwt config. Refresh rotation giữ family/hạn tuyệt đối; replay token cũ revoke family. Middleware kiểm live DB user/organization/family/role/tenant bằng một query sau xác minh JWT, không cache quyền. Logout revoke family hiện tại; logout-all revoke mọi family và tắt push bindings atomic. [Đo/tối ưu auth](../docs/auth-performance.md); [cleanup gate](../docs/refresh-token-cleanup.md).
- Profile cá nhân có DOB/gender/phone, GET/PATCH organization đã có ETag; Avatar có upload IFormFile, decoder và cleanup/recovery. Candidate lease kiểm clock DB sau khóa, bounded S3 read và PG recovery/race/rollback có test. Swagger source đã có PATCH field/null semantics, multipart file, proof/enum/header và metadata đúng; deployment binary mới còn phải kiểm riêng. [Checklist endpoint auth](../docs/auth-api-checklist.md), [test Avatar/signed URL](../docs/avatar-manual-test.md). Signed S3 URL đọc còn hạn không cần API Bearer; API cấp URL vẫn cần Bearer.
- Google UID đã link trả Authenticated; Google mới OnboardingRequired kèm proof15 phút, email trùng ACCOUNT_LINK_REQUIRED. Anonymous `/api/auth/google/onboarding/complete` tạo Google-only Trainee/OrganizationUser atomic cùng receipt/session/Create+Login audit, lần đầu201 Authenticated; cùng input24 giờ409 ONBOARDING_ALREADY_COMPLETED. Mất response thì exchange Firebase token mới; không lưu/phát lại bearer từ receipt. Nested onboarding chứa verified email/displayName, root proof/expiry alias deprecated. Explicit `/api/me/link-google` yêu cầu live session + currentPassword + Google token, giữ email/role/tenant, link mới revoke families/reset proof và tăng revision/audit atomic; không tự ghép email hoặc coi Google mới đã tạo account.
- Provider/FE/deployment cần kiểm chứng riêng. Đọc checklist trước task auth và bảo toàn OTP, password/session/ETag invariants; không suy Redis/Hangfire cần thiết từ luồng này.

## Kiến trúc đích đã thống nhất — 2026-09-17

- Backend dùng C# + ASP.NET Core trên .NET; EF Core/Npgsql kết nối Supabase Database (managed PostgreSQL). Supabase Auth không được dùng.
- Web/Mobile dùng email/password do BE quản lý hoặc Google Sign-In qua Firebase. API xác minh password/Fire3D session hoặc Firebase ID token rồi lấy role, trạng thái và `organizationId` từ PostgreSQL; không tin role/tenant do client gửi hoặc custom claim đơn lẻ.
- Schema đích lưu password/refresh/reset token hash và `firebase_uid` nullable khi liên kết Google; không lưu plaintext password, Google refresh token hoặc FCM credential. Backend vẫn sở hữu launch grant, signed URL và authorization nghiệp vụ.
- BE chịu trách nhiệm register trainee/organization, username global unique, profile ETag, organization name/address/phone, password change/reset/link Google và avatar S3 intent/complete/delete. Register/profile/password/Avatar đã có đường thực thi; Google onboarding/link đã có source; Firebase/client/deployment chưa nghiệm thu. Kiểm tra invariant và deployment theo luồng auth hiện có bên trên, không coi context/SQL là migration đã chạy.
- FCM dùng cho push notification; FCM registration token là token nhận push của installation, không phải token đăng nhập. `registrationToken` trong API register là proof email sau OTP, không phải FCM token.
- Raw IFC, manifest và content package nằm trong AWS S3 private; backend cấp signed URL TTL ngắn. Không đưa AWS, Firebase Admin hoặc Supabase service-role secret vào client.
- Azure đã được chọn cho AI/RAG FastAPI service; compute cho BE, IFC/Blender worker và Unity Editor worker, cùng SKU/region/cost, vẫn phải spike và chốt riêng. Không suy ra toàn bộ hệ thống chạy Azure.
- OneShield thuộc hệ thống OnePortal của iNET là lớp edge/bảo vệ phía trước Nginx. Nginx tiếp nhận HTTP(S) từ edge và chuyển request vào .NET; backend vẫn là nơi xác minh identity, tenant, quota, scope và gọi AI nội bộ. OneShield không thay thế authorization. Plan/SKU, DNS, TLS termination, WAF/rate limits, upstream, health check và vị trí chạy Nginx là việc triển khai còn lại.
- Backend là authority cho Building service entitlement: kỳ 6 hoặc 12 tháng, ngày hiệu lực/hết hạn, trial, publish/session gate và lịch sử price/terms snapshot. Một organization có nhiều Building với kỳ khác nhau; không suy ra auto-recurring PayOS.
- Payment flow mục tiêu là quotation/request → PayOS checkout → verified webhook → đối soát → payment record → grant/extend đúng Building. `returnUrl`/`cancelUrl` chỉ điều hướng; retry/webhook trùng không cấp quyền hoặc ghi tiền trùng.
- Backend sở hữu AI quota/usage trả trước: organization quota pooled giữa các Building và top-up đã thanh toán; ledger theo organization/building/user/audience/request type. Trainee daily quota riêng. Hết quota chặn request tính phí mới; không sinh nợ trả sau.
- Backend lưu `ai_requests` bền vững với canonical input hash, status/result/citations/model và request scope. Tạo input qua `create_ai_request`; ghi result qua contract result/reconcile trước khi settle quota. Reservation allocations phân bổ nhiều grant Organization và giữ provenance. Không dùng `SKIP LOCKED` để kết luận hết quota; AI không tính tiền.
- Quota grant phải phân biệt audience `organization` và `trainee`: Trainee daily grant gắn trực tiếp user, không yêu cầu organization. Usage liên kết grant, request idempotency và policy snapshot; grant trả trước có provenance payment/quotation line, không có settlement period trả sau.
- QR là canonical Building QR → list published trainings → session preparation pin selected training/release/scenario → explicit online session start. Preparation không cấp quyền; `POST /api/training/sessions/{sessionId}/start` kiểm tra access, approved/published scenario, entitlement, learner seat, package/runtime và idempotency, sau đó launch grant pin identity bất biến. Backend phải allow continuation/sync after network loss và heartbeat analytics.
- OrganizationUser có thể chạy playtest draft/version riêng; start kiểm tra Trial còn quota thử hoặc Building entitlement Active. Backend cấp playtest grant/session type đúng tenant, không tạo learner analytics, không dùng QR public và không coi Trial là quyền publish/session Trainee.
- Dashboard định nghĩa riêng unique trainees, total plays, active sessions heartbeat, completion/duration và building usage; OrganizationUser chỉ thấy tenant của mình.
- `total plays` chỉ tính learner sessions đã bắt đầu; playtest/session chuẩn bị bị loại. `active sessions` là ước tính từ heartbeat trong cửa sổ cấu hình và phải dùng cùng định nghĩa trong API/requirements/dashboard.
- Publish chỉ được phép khi scenario/rubric version có Admin approval đúng hash, Building còn entitlement và package có checksum/manifest/runtime tương thích, ValidationRun Passed và không còn Error/Critical issue mở; payment Applied có provisioning key ổn định và record reconcile nếu cấp entitlement lỗi.
- Quota reserve/chốt/hoàn phải là transaction PostgreSQL ngắn với row lock hoặc conditional update; không giữ transaction khi chờ AI/PayOS/S3/worker. Transactional outbox, worker lease/attempt và reconcile xử lý eventual consistency giữa service.
- Runtime start dùng actor-bound backend function, server-owned compatibility catalog và semver `major.minor.patch`; `SECURITY DEFINER` start functions không được execute bởi `PUBLIC`. Người khác không thể launch bằng session ID hoặc update trực tiếp trạng thái chuẩn bị.
- Transaction boundary là transaction PostgreSQL ngắn cho reserve/provision/idempotency; LLM, PayOS, S3 và worker nằm ngoài transaction. Timeout AI chuyển `NeedsReconcile`; outbox/lease/attempt fencing và reconcile xử lý retry, không ghi đè kết quả stale. Heartbeat lưu server-received time, event dùng stable ID + sequence/schema để deduplicate.
- `origin/main` đã từng có triển khai password/JWT/password reset. Đây là hiện trạng cần đối chiếu, không phải hướng bị thay bằng Firebase: local password/session vẫn giữ; Firebase chỉ bổ sung Google identity verification. Task triển khai sau phải lập migration/rollback và kiểm thử token verification, account mapping, revoke/disable và dữ liệu hiện có.

## Chạy và kiểm tra

Từ gốc BE, với .NET 10 SDK/phụ thuộc sẵn sàng:
- `dotnet build Fire3D/Fire3D.slnx`: kiểm tra biên dịch, không thay thế integration test.
- `dotnet run --project Fire3D/Fire3D.API/Fire3D.API.csproj`: chạy API khi task cần và đã có cấu hình database phù hợp.
- Solution có `Fire3D.AuthTests` và `Fire3D.IfcTests`; chọn đúng test project/filter theo thay đổi. Sự tồn tại của test project không chứng minh test đã chạy hoặc toàn bộ nghiệp vụ đã đạt.
- Với thay đổi database/enum, cần kiểm tra có mục tiêu trên database test được phép dùng; không tự kết nối production.
- Ghi bằng chứng build/test của từng task trong handoff local hoặc PR; context này không phải báo cáo kiểm thử.

## Thiết kế liên quan

Khi có Docs bên cạnh, đối chiếu `fire_evacuation_schema.sql`, `fire_evacuation_erd.md`, requirements và workflows theo task. Trước khi lập việc sửa BE theo contract, đọc [implementation checklist](../docs/api-implementation-checklist.md); đây là backlog có baseline code cần được kiểm tra lại khi bắt đầu task. Dùng [API guide](../docs/api-docs.md) để biết route source hiện có. Docs giữ ba vai trò PlatformAdmin, OrganizationUser, Trainee; readiness nội bộ không phải phê duyệt PCCC. Nếu clone độc lập thiếu tài liệu bắt buộc, báo thiếu thay vì đoán schema hoặc tự clone.

## Invariant SQL/contract bổ sung — 2026-09-18

- Runtime compatibility của publish, Trainee start và playtest dùng một helper fail-closed; thiếu minimum runtime, protocol, manifest schema hoặc capability metadata bị từ chối. Start replay yêu cầu idempotency key và runtime payload giống nhau.
- Contract v7 loại AI billing period/AIUsage invoice: payment gói/top-up cấp quota trả trước idempotent; request chỉ consume/release reservation của quota đã có.
- Trigger AI request phải chạy `BEFORE INSERT OR UPDATE`; trigger INSERT kiểm tra role/tenant/policy, còn request đã tiếp nhận được reconcile dù user bị khóa. Worker claim/renew/accept phải fence current attempt và provenance.

## Contract hardening — 2026-09-18

- Technology/SQL v7 là nguồn chính cho prepaid provisioning và request quota. Payment/quotation/grant giữ snapshot và provenance; usage muộn/không chắc chuyển request reconcile, không tạo hóa đơn cuối kỳ.
- Reserve/settle dùng lock order `request → ledger/reservation → grants theo id tăng dần`. Không giữ transaction khi gọi AI/PayOS/S3/worker. Terminal AI result/policy/input provenance không sửa lịch sử; user bị khóa sau accept vẫn reconcile qua contract result. Không mang period lock/consent từ thiết kế trả sau cũ vào v7.
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

- Contract đích: Trainee local gửi username ngay khi đăng ký; OrganizationUser gửi hồ sơ organization (tên, địa chỉ, điện thoại), username cá nhân tùy chọn theo yêu cầu đích nhưng chưa được route hiện tại nhận. Google mới chọn loại tài khoản qua onboarding proof15 phút; complete lần đầu trả phiên sau commit; cùng input receipt24 giờ trả409 AlreadyCompleted để FE exchange recovery. AccountType trainee/organization, alias Trainee/OrganizationUser; không số enum/PlatformAdmin. Account đã link không chọn lại role/tenant và không tự gia nhập organization có sẵn.
- Backend là authority cho username lowercase unique, profile ETag, password/session revoke, Google link, S3 avatar và organization profile. Game start không còn ProfileIncomplete username gate.
- Organization có nhiều Building. `quotation_building_items` là nguồn dòng dịch vụ theo Building; giá/discount/terms snapshot ở quotation/item, payment một lần có thể provision nhiều entitlement bằng key từng item. `enterprise_quote_requests` không tạo charge/entitlement trước quotation/payment.
- Background task tạo notification web/email trước 5 ngày theo entitlement/kỳ/kênh; Mailgun là kênh email, PostgreSQL/outbox giữ idempotency/retry. Discount không cộng dồn và không sửa lịch sử quotation.

## Regression corrections — 2026-09-19

- Target quotation header no longer contains `building_id`, `service_package_id` or `service_duration_months`; `quotation_building_items` is the only BuildingService line source. Any current EF/entity references are migration work, not a reason to reintroduce duplicate fields.
- Learn editorial orchestration uses a durable application-command receipt keyed by actor/operation/idempotency key and canonical input hash. The .NET service locks post → version → links, then writes state, audit and outbox atomically; SQL re-reads version status after acquiring locks.
- Password reset/change source đã dùng user advisory/database transaction để consume token, đổi password, revoke session và ghi audit; `OnTokenValidated` kiểm tra family khi xác thực access JWT. Đối chiếu lại source khi task sửa auth; vẫn cần kiểm tra trên schema target, lỗi rollback và race trên PostgreSQL test. Docs technology có ghi chú trạng thái handler cũ; không lấy ghi chú đó làm bằng chứng source hiện tại.
- Google onboarding completion must persist the created `user_id` and canonical input hash on the short-lived onboarding record so a retried completion returns ONBOARDING_ALREADY_COMPLETED and recovers through verified Google exchange instead of creating a second user, organization or issuing tokens from a receipt; it never stores a password or bearer token.
- Current SQL/Docs updates are design-only. Do not claim database execution, permission, concurrency, S3 or email recovery tests passed.
- Quotation target lifecycle records `accepted_at` once on `Issued → Accepted`; quotation lines cannot move between quotations after creation. The design grants the PayOS ledger owner only the row-lock privilege it needs, gives the processing owner attempt INSERT, and gives the backend executor the minimum auth/profile/Building write path; these privileges still require database execution tests.

## Recovery gate corrections — 2026-09-19

### Local auth / Google exchange implementation — 2026-10-05

- Email lookup hiện dùng `lower(btrim(email))` theo normalized index, input trim/lowercase. Change-password lấy family từ JWT và recheck dưới khóa lifecycle/user; reset/change tiêu thụ reset local/legacy, revoke families và audit atomic. Migration `20261005090000_AddPasswordRecoveryGate` thêm restricted SECURITY DEFINER gate `invalidate_legacy_reset_tokens(uuid)` đánh dấu used_at, không DELETE lịch sử. Migration mới chỉ kiểm trên PostgreSQL disposable, chưa áp Supabase trong đợt này.
- Google exchange dùng Firebase Admin SDK checkRevoked, bắt buộc Google provider/email verified; deadline15 giây, invalid identity401, provider unavailable503, giữ request cancellation. Sau khóa recheck user ID/UID và lifecycle. Onboarding completion đã được bổ sung sau hai task nền; explicit link dùng proof local + Google và giữ identity hiện có, yêu cầu login lại khi link mới. S3 không thuộc đợt Google này. Tài liệu test tay: `docs/auth-local-google-manual-test.md`; kết quả từng lượt test/handoff đặt ở `.codex/local`, không suy ra provider/deployment đã nghiệm thu.

- Session preparation uses the dedicated executor with read access to its referenced identity, Building, training, package and validation rows. Session result/event writes go through `record_session_event` and `complete_training_session`; playtest completion goes through `complete_playtest_session`. Replay is keyed by event ID/sequence or result/completion key/hash and does not re-check entitlement or current user activity after start.
- Playtest creator activity is checked at preparation/start. A playtest already started may complete or sync after the creator is disabled or the entitlement expires.
- Processing workers register artifact/validation/issues through lease-bound `register_processing_output`; they do not receive direct provenance-table DML. Prepaid provisioning uses `provision_ai_topup_v7` and `provision_building_line_v7`; request quota reserve/settle remains replay-safe.
- These are target SQL/contract changes only; permission, concurrency, recovery and runtime execution tests remain unrun.
- Quotation target lifecycle records `accepted_at` once on `Issued → Accepted`; quotation lines cannot move between quotations after creation. The design grants the PayOS ledger owner only the row-lock privilege it needs, gives the processing owner attempt INSERT, and gives the backend executor the minimum auth/profile/Building write path; these privileges still require database execution tests.


### Selected Building/IFC/scenario/playtest/release/support implementation

- Building tenant/validation, verified immutable IFC intent/source, leased HTTP worker/outbox/retry, immutable v7 scenario authoring, exact technical readiness, separate immutable content approval and session-bound playtest start/grant are implemented. Transport defaults to PostgreSQL outbox → authenticated HTTP worker. Optional RedisStreams adds a leased publisher and BE bridge; [Redis delivery](../docs/redis-processing.md) keeps PostgreSQL authoritative, ACKs only durable handoff receipts and recovers/retains protected entries. Cache/auth rate limits/Hangfire are not implemented.
- Built release now derives immutable accepted ReleasePackage/manifest metadata, checks exact readiness and Approved content/rubric and creates package/provenance/matching Training/receipt/audit atomically. Built does not certify Unity execution. Publish now uses a restricted paid-entitlement/readiness/approval/package gate; Publishing:Enabled defaults false for rollout.204/replay when eligible,409 business gates,503 when disabled.
- Building defaults Private. Random participation code is stored hashed; account-bound grants pin access revision and are invalidated by visibility/code changes. Logged-in Trainee Training list requires current access plus Active/Published/pinned approval. Listing never grants learner seat/start.
- Support service/store rechecks lifecycle/session/owner/admin under locks. Create/message use canonical receipts, PATCH If-Match, detail ETag, append-only message history and bounded pages. Resource tenant is used for audit.
- Use docs/selected-api-contract.md, docs/api-route-inventory.md and per-feature manuals for source/test versus shared schema/provider/deployment evidence. Migrations must precede matching binary; grant-only changes can break old direct-write handlers. Do not turn fake worker/provider output into production acceptance.
- Learner start/sync/result and real IFC/Blender/Unity acceptance remain incomplete. Publish source gate is implemented in the current billing/reporting branch; deployment/feature enablement are separate. Auth/OTP/Avatar/payment business contracts are unchanged by this selected work.

- Final selected API review: IFC receipt replay checks the persisted Building ID in SQL, preserving existing canonical hashes; the HTTP store already included Building ID. Scenario object anchors require a current Succeeded Geometry attempt with matching Passed validation and no Error/Critical blocker. Forward-only migrations 20261007130000/20261007140000 preserve existing data and gate ownership/ACL; rollout evidence belongs in docs/ifc-authoring-deployment.md.

- Selected final verification 2026-10-07: 36 selected HTTP/PostgreSQL tests and 105 supported IFC tests passed; solution build0 warnings/errors. Supabase history now20261007140000 with final read-only ACL/count checks. Real providers, binary/client smoke tests, publish and learner pipeline remain uncertified.

## Publish, billing v7 và reporting — contract triển khai

Implementation includes immutable commercial quota policies,6/12-month monthly-priced catalog, fixed quotation periods/seats/quota, checkout period reservations and atomic paid entitlement/quota provisioning. Dedicated commercial policy/grant tables do not implement AI usage reserve/consume. Legacy recovery remains; no new checkout from incomplete legacy quote snapshots. Upgrade/top-up/learner seats are pending.

Publish uses a restricted gate with live family, paid entitlement, exact approved content/rubric/readiness and current accepted package/runtime provenance. Publishing:Enabled defaults false. Audit adds safe changes/target filters; operations analytics use readonly RepeatableRead and tenant/caller scope. OpenAPI inventory is generated; health/version falls back to assembly artifact version. See docs/billing-v7-rollout.md, docs/reporting-operations.md and docs/publish-billing-v7-manual-test.md.

Source/test evidence does not establish Supabase/Azure/provider/Unity/frontend acceptance. Check deployment evidence separately. Do not apply the full target schema, reset data or enable v7 sales before all worker/API instances and restricted grants are ready.

## Worker and selected mutation contracts

Pending registration cleanup uses a bounded SECURITY DEFINER gate with a restricted NOLOGIN owner, preserving verified accounts and business/audit history. Avatar cleanup uses enqueue/claim/reference/renew/complete/retry gates under RLS; protected jobs remain durable and stale leases cannot acknowledge another claim. Permission failures are blocked work with a 60-second retry. Matching migration and worker/API rollout are required; old direct-write workers are incompatible with revoked queue/account DML.

Readiness confirmation/technical rejection/content decisions and catalog/discount/quota-policy/enterprise mutations recheck an owned live session family after lifecycle/actor/resource locks. JWT family is internal and excluded from receipt input hashes; the old family-less readiness entrypoint fails closed. New readiness/approval audit snapshots project safe field changes; legacy unrecognized snapshots remain metadata-only.

Editor preview checks S3 HEAD and recorded size before signing; missing/mismatched objects are NotReady and storage failures return503 PREVIEW_STORAGE_UNAVAILABLE. Annotation anchors use the accepted current Geometry reference gate, avoiding a dependency on a bim_facts table absent from migration history. OpenAPI headers derive from endpoint parameter/response metadata, not controller allowlists. See docs/api-worker-contract-manual-test.md and docs/worker-permissions.md. Learner lifecycle/training analytics and real providers/toolchains remain separate pending work.
