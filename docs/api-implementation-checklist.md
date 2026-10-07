# Danh sách sửa BE theo contract trong Docs

Tài liệu này là backlog đối chiếu code BE với chuẩn nghiệp vụ trong workspace [`Docs`](../../Docs/README.md). Contract gốc nằm ở [requirements](../../Docs/fire_evacuation_requirements.md), [workflows](../../Docs/fire-evacuation-training-workflows.md), [technology](../../Docs/fire-evacuation-training-technology.md), [schema SQL](../../Docs/fire_evacuation_schema.sql) và [ERD](../../Docs/fire_evacuation_erd.md). API guide mô tả hành vi source đang có; checklist này ghi phần còn phải sửa. Không coi tên entity, route hay thiết kế SQL là bằng chứng tính năng đã hoàn tất.

## Kiểm chứng deployment bổ sung ngày 03/10/2026

Các trạng thái lịch sử bên dưới chỉ có giá trị trong phạm vi baseline ghi kèm; bằng chứng mới về PayOS/deployment nằm tại [payos-deployment.md](payos-deployment.md).

- ✅ Supabase đã nhận sáu migration PayOS, migration normalized email và login/grants giới hạn quyền cho test local; không reset dữ liệu.
- ✅ API local tạo checkout/QR PayOS thật 100.000 VND, create201/replay200 cùng checkout/order; chưa chuyển tiền.
- ✅ Azure Swagger/OpenAPI/health/return/cancel200; preflight OTP và PayOS cho cả hai domain FE204, có CORS đúng. Create thiếu Bearer401, webhook thiếu chữ ký400, OTP/resend email sai400 có lỗi theo field.
- ❌ Chưa nghiệm thu worker/executor trên Azure, gửi OTP thật, webhook hợp lệ, Paid/Applied và provisioning/entitlement thật.
- Finding Swagger tại baseline deployment 03/10: multipart/PATCH/enum/proof/header sai hoặc thiếu. Phần **auth** đã sửa source bằng schema/operation transformer và có contract test ngày 05/10; binary/OpenAPI deployment mới chưa kiểm chứng. Response GET PayOS và các module ngoài auth không thuộc đợt này.

## Cập nhật auth source/test — 05/10/2026

[Checklist 28 endpoint auth](auth-api-checklist.md) ghi riêng code/test/provider. Avatar đã chặn lease hết hạn và có PostgreSQL recovery/rollback/race; OpenAPI auth khai báo đúng schema PATCH, multipart file, proof bắt buộc, enum tên, If-Match và installation proof. Google source đã có onboarding/link; chưa áp migration mới lên Supabase hoặc test Firebase/S3 thật. Không dùng tình trạng deployment lịch sử để đánh dấu binary mới đã nghiệm thu.

## Baseline và trạng thái

Organization phone uniqueness có source cho email/Google/PATCH: `409 ORGANIZATION_PHONE_EXISTS`, field error đúng DTO; [preflight chỉ đọc](../database/006_organization_phone_preflight.sql) và migration additive chặn duplicate canonical trên mọi tổ chức, không sửa phone/revision legacy. PostgreSQL disposable kiểm cross-flow, race, proof rollback/retry, lifecycle, runtime role và migration history. Supabase đã áp migration/index ngày 06/10/2026 sau khi người dùng xử lý phone legacy thànhNULL; binary deployment/FE chưa nghiệm thu. [Kịch bản test tay và bằng chứng DB](organization-phone-manual-test.md) tách riêng các bước này. Không áp uniqueness cho phone cá nhân.

Đồng bộ contract ngày 04/10/2026 theo [Docs v7](../../Docs/schema_v7_contract.md), Docs commit `2b56ba3`; đối chiếu source BE `origin/develop` tại `e42a2eb`. Các kết quả test/deployment ngày 02–03/10 giữ baseline và giới hạn riêng. Trước khi triển khai mỗi work item phải xác nhận lại source; lần rà tài liệu này không chạy lại application tests hoặc provider.

- **GAP:** capability/contract còn thiếu hoặc source đang lệch.
- **VERIFY:** đã thấy code cho một phần contract; cần kiểm thử và/hoặc khớp schema/provider trước khi kết luận.
- **LATER:** requirement có trong Docs, triển khai sau dependency nêu trong thứ tự công việc.
- **PARTIAL / CODE/TEST:** source có một phần hoặc có bằng chứng kiểm thử lịch sử; không đồng nghĩa đã triển khai đầy đủ contract v7.
- Mỗi task triển khai cập nhật checklist, API guide và bằng chứng trong PR. Chỉ cập nhật trạng thái sau khi kiểm cả quyền, tenant, trạng thái, persistence, audit/idempotency và lỗi liên quan.

## A. Database và nền tảng dùng chung

### DB-02 — P0/P1 · CODE/TEST · Backend ACL, IFC enqueue và token cleanup

- **Đã sửa source:** Migration forward-only `HardenBackendObjectPermissions` chặn explicit Supabase anon/authenticated grants trên 6 bảng backend và PayOS SECURITY DEFINER entrypoints; giữ grant executor, thêm backend RLS và sửa default ACL của migration owner. `AddIfcIntegrationOutbox` giao canonical tenant enqueue; `AddRefreshCleanupGate` cho API dọn family hết retention mà không có direct DELETE. [Hardening](database-hardening.md), [outbox](ifc-outbox.md), [cleanup](refresh-token-cleanup.md).
- **Kiểm chứng:** PostgreSQL cô lập có test quyền backend/executor, rollback/replay outbox, retention và lock recheck cleanup. Full-chain Auth fixture và non-superuser owner-transfer có kiểm tra riêng. Schema này không phải bootstrap toàn bộ v7.
- **DB đã kiểm chứng:** 05/10/2026 áp đúng 3 migration lên Supabase hiện có, history 29→32; giữ 4 account/16 refresh token, executor grants và kiểm read-only bằng API identity giới hạn quyền. [Bằng chứng và giới hạn](database-hardening.md#deployment-evidence--2026-10-05). **Còn phải nghiệm thu:** Azure binary, latency thực tế, worker IFC/provider thật; DB update không chứng minh các phần này hoạt động. Quyền API không chuyển thành postgres.

### AUTH-PERF — CODE/TEST · Đo latency và giảm round trip

- Password verify/rehash ngoài lock, snapshot email/hash và lifecycle được kiểm lại dưới lock; login/session/audit và reset fence atomic. Middleware session authorization còn một SQL query, vẫn kiểm quyền live và family.
- [Metric và giới hạn](auth-performance.md). Test không chứng minh p95 login Azure đã giảm; không thêm index trùng, giảm hash strength hay cache quyền. Google onboarding/link có source và regression, AUTH-02 chưa nghiệm thu Firebase/client/deployment.

### DB-01 — P0 · GAP · Schema mapping

- **Contract/current:** SQL v7 là đích; EF/migration/runtime chưa đồng bộ toàn bộ. `LocalPasswordReset` dùng `local_password_reset_tokens`, đã được v7 biểu diễn; EF vẫn giữ `password_reset_tokens` legacy. Chưa được suy môi trường hiện tại trống từ nhận định tháng 09; xem bằng chứng Supabase/PayOS phía trên.
- **Sửa code:** Lập mapping entity/enum/query/grant sang v7, kiểm tra schema/dữ liệu từng môi trường và đường gọi reset trước khi xử lý bảng legacy. Dựng database test mới theo schema đích; không dùng `EnsureCreated` thay SQL/gates. Schema v7 đã syntax-load trên PostgreSQL disposable với stand-in pgvector theo contract Docs; cần kiểm actual extension/quyền/runtime riêng.
- **Nghiệm thu:** Database test mới dựng được từ schema mục tiêu; enum/FK/constraint/grant và truy vấn task chạy đúng. Migration/backfill của môi trường khác phải dựa trên dữ liệu đã kiểm tra; không chạy destructive SQL lên DB dùng chung để kiểm chứng tài liệu.

### AUTHZ-01 — P0 · GAP · Actor và tenant

- **Contract/current:** Mọi thao tác phải kiểm actor, role, tenant và trạng thái resource ở BE. Một số luồng dùng `Guid.Empty` làm scope rộng hoặc dựa vào tenant claim mà chưa xác minh quan hệ resource.
- **Sửa code:** Rà controller, handler và store; lấy actor từ phiên đã xác thực, xác minh tenant qua quan hệ trong DB và áp dụng permission matrix của Docs. Bỏ fallback ID rỗng có thể tắt tenant check.
- **Nghiệm thu:** Tenant đúng được phép; tenant khác, Trainee không đủ quyền hoặc account/organization/Building không hoạt động bị từ chối. Lỗi không làm lộ resource ngoài scope; audit có đúng actor.

## B. Tài khoản và xác thực

### AUTH-01 — P1 · PARTIAL · Đăng ký local

- **Contract/current:** FE nhập form trước → OTP → một nút gọi verify-otp rồi register với toàn bộ form + proof. `POST /api/auth/registration/request-otp` tạo challenge/job gửi mã nhưng không tạo identity; `POST /api/auth/resend-verification` gửi OTP mới sau cooldown và vô hiệu mã/proof cũ. Email đã có account trả 409 EMAIL_EXISTS + errors.email, không enqueue. Ba route register consume proof trong transaction tạo role/tenant server-owned và audit; form sai không consume proof. `/check-email/` là demo OTP, FE thật cần tích hợp riêng. `/verify-email` chỉ dành link pending legacy. Migration `20261003030000_AddNormalizedRegistrationEmail` bổ sung unique email trim/lowercase và guard legacy duplicates, không xóa/gộp dữ liệu; migration OTP/username cũ giữ nguyên. `/api/auth/register` là alias Trainee. [Test tay](registration-payos-manual-test.md); mức nghiệm thu DB/provider/FE phải ghi riêng.
- **Còn thiếu:** username cá nhân tùy chọn cho đăng ký OrganizationUser. PATCH profile tổ chức đã có ở `OrganizationProfileController`; email verification, profile ETag, đổi username và avatar cần nghiệm thu provider/deployment riêng.
- **Nghiệm thu cần chạy:** HTTP/PostgreSQL disposable kiểm tra proof không tạo user trước verify, proof chỉ dùng một lần, hai role, organization transaction và collision username khác hoa/thường. Còn cần test race concurrent cùng username và chuyển dữ liệu production trước khi đóng hoàn toàn.

### AUTH-02 — CODE/TEST · Google onboarding và link; deployment chưa nghiệm thu

- **Contract/current:** [ExchangeFirebaseTokenCommand](../Fire3D/Fire3D.Application/Authentication/Commands/FirebaseLogin/ExchangeFirebaseTokenCommand.cs) xác minh Google và trả `OnboardingRequired` cho identity mới; đã có proof15 phút và `/api/auth/google/onboarding/complete`, tạo Google-only Trainee/OrganizationUser + receipt/login/refresh-hash/Create+Login audit atomic, trả201 Authenticated. Nested proof có verified email/displayName và alias deprecated. Replay cùng input24 giờ trả409 ONBOARDING_ALREADY_COMPLETED → exchange recovery, không cấp thêm phiên. Invalid/expired split400; quota/lock timeout có Retry-After. `/api/me/link-google` yêu cầu live family/local password/verified Google; link mới revoke sessions/reset proofs và tăng profile revision + audit atomic, giữ role/tenant/email, cùng UID replay với session mới không duplicate audit.
- **Exchange đã củng cố:** [FirebaseIdentityProvider](../Fire3D/Fire3D.Infrastructure/Authentication/FirebaseIdentityProvider.cs) dùng SDK/revocation, Google provider và email verified, timeout15 giây;401 invalid khác503 unavailable. Recheck UID owner/lifecycle dưới khóa, giữ response Authenticated/OnboardingRequired/ACCOUNT_LINK_REQUIRED. [FirebaseGoogleVerificationTests](../Fire3D/Fire3D.AuthTests/FirebaseGoogleVerificationTests.cs) kiểm claims/revoked/timeout/cancellation; [GoogleExchangeHardeningTests](../Fire3D/Fire3D.AuthTests/GoogleExchangeHardeningTests.cs) kiểm ownership/lifecycle và không auto-link. Test SDK giả lập, không xác nhận Firebase production.
- **Đã triển khai:** proof15 phút và replay24 giờ, Google-only Trainee/OrganizationUser; link explicit không auto-match email, không replacement/chiếm UID; lock lifecycle → UID → user.
- **Nghiệm thu:** Kiểm tra UID mới/đã link, email local chưa link, token hết hạn, retry cùng/khác input và hai request đồng thời. Không tạo user/organization trùng hoặc đổi role/tenant tài khoản đã link.

### AUTH-03 — P1 · PARTIAL · Profile, ETag và avatar

- **Contract/current:** `GET/PATCH /api/auth/me` trả/nhận ETag theo `profile_revision`; PATCH hỗ trợ full name, username, dob, gender và phone. `/api/me/avatar` có intent/complete/GET/delete, complete copy theo S3 ETag và không dùng chung final key giữa các request. `GET/PATCH /api/organizations/me` trả ETag và PATCH chỉ thay đổi field được gửi.
- **✅ Code/test Avatar:** Candidate được lưu trước conditional copy; reserve/finalize chặn lease hết hạn theo clock DB sau khóa. Có PostgreSQL disposable test restricted role/RLS, rollback audit, cleanup/retry/protection, complete/delete cạnh tranh; SDK signing offline và stream S3 giả lập có test. [Test tay và giới hạn](avatar-manual-test.md).
- **❌ Chưa nghiệm thu:** S3 thật/IAM/CORS, request treo vượt cleanup grace, FE mở signed URL và vận hành deployed worker. Không lập lại task bổ sung candidate path đã có; không suy IAM/object tồn tại từ URL ký được.
- **Nghiệm thu:** Lưu thành công trả ETag mới; ETag cũ không ghi đè thay đổi; không sửa được role/email/tenant/status; username, organization scope và quyền sở hữu object S3 được kiểm tra.

### AUTH-04 — P1 · CODE/TEST · Reset và change password

- **Contract/current:** Forgot/reset gửi qua worker Mailgun; change xác minh mật khẩu cũ và live family lấy từ JWT dưới lifecycle/user lock. Reset/change consume/invalidate token local và legacy, đổi hash, revoke session và audit atomic. Migration `AddPasswordRecoveryGate` cấp EXECUTE gate legacy cho backend, giữ lịch sử và không cần direct DELETE. Forgot/link/reset kiểm lifecycle; Google-only không được thêm password qua reset. Lookup email dùng trim/lowercase tương thích index/dữ liệu legacy.
- **Sửa code:** Giữ reset và change là hai luồng riêng. Hoàn tất mapping sang schema đích; kiểm tra rollback, race login/reset và mọi refresh-token family. Không coi có worker là bằng chứng Mailgun production đã gửi thành công.
- **Nghiệm thu:** Có PostgreSQL disposable regression cho restricted runtime role, reset replay/race/audit rollback, organization bị khóa, email legacy và change chờ khóa/family revoked. Migration database dùng chung và Mailgun/deployment thật cần nghiệm thu riêng; không đóng provider từ DB test.

### AUTH-05 — P1 · VERIFY · FCM device token

- **Contract/current:** AuthController có đăng ký/xóa device token và adapter FCM; v7 biểu diễn `device_installations` và binding user/session riêng. Code proof/family binding đã có, cần đối chiếu persistence/grants.
- **Sửa code:** Rà ownership theo user/installation, rotate/revoke, validate token và che token khỏi log. FCM chỉ dùng push, không dùng để đăng nhập.
- **Nghiệm thu:** Nhiều thiết bị, token invalid/replaced và user khác xóa token đều được xử lý đúng. Unit/mock không được ghi là bằng chứng FCM thật.

## C. Building, IFC, authoring và release

### ACCESS-01 — P1 · GAP · Quyền tham gia Building (FR-TRAINING-04)

- **Contract/current:** V7 mặc định Building Private; source chưa có capability participation grant/access revision tương ứng.
- **Sửa code:** List/package/prepare kiểm Trainee đăng nhập và quyền Building; verify mã hiện hành tạo account-bound grant. Rotate/revoke mã hoặc đổi visibility tăng access revision, vô hiệu grant cũ. Public vẫn cần Trainee đăng nhập; QR không tạo tenant membership.
- **Nghiệm thu:** Mã sai hoặc grant user khác bị từ chối; grant cũ mất hiệu lực sau thay đổi; start recheck quyền. Phiên đã start giữ quyền sync theo snapshot.

### LIBRARY-01 — P1 · LATER · Thư viện Organization (FR-LIBRARY-01–02)

- **Contract/current:** Chưa thấy module library v7 trong source; catalog scenario cũ không chứng minh thư viện đã có.
- **Sửa code:** PlatformAdmin maintain template/rubric mẫu/metadata thiết bị có version; OrganizationUser đọc trong console để author. Template tùy chọn, capability chỉ chọn runtime hỗ trợ; tách Learn và kho riêng tenant.
- **Nghiệm thu:** Organization không quản trị library; sửa mẫu không sửa bài đã phát hành; private IFC/scenario không tự vào thư viện; capability không hỗ trợ bị từ chối.

### APPROVAL-01 — P1 · SOURCE/DB TESTED · Duyệt scenario/rubric (FR-SCENARIO-03)

- **Contract/current:** Exact immutable scenario/rubric submission and Admin approve/reject implemented with receipt, audit and live tenant/lifecycle checks. Technical readiness is independent. See [scenario-readiness.md](scenario-readiness.md).
- **Remaining acceptance:** Real client/deployment and integration into the release/publish gate. Legacy versions receive no fabricated approval.

### IFC-01 — SOURCE/HTTP/DB TESTED · Bound source/outbox/worker contract

Verified immutable intent/source, canonical transactional outbox, HTTP dispatcher, machine-authenticated leased/fenced worker gates, accepted provenance, retry and recovery implemented and tested on isolated migration history with fake S3/HTTP worker. Supabase schema through Task6 applied; no real IFC/Blender/Unity output/deployed worker certification. See [processing-worker.md](processing-worker.md), [selected checklist](selected-api-contract.md).

### IFC-02 — P1 · SOURCE/DB TESTED · Readiness theo revision/version

- **Contract/current:** Confirm requires exact scenarioVersionId/validationRunId/annotationSetId, accepted current worker attempt/artifact, matching hashes, Passed/runtime ready and no Error/Critical blocker. Returns reviewId, atomic receipt/audit; technical rejection preserves history.
- **Remaining acceptance:** Built release consumes that exact confirmation and matching immutable approval/provenance. Fake worker evidence is not geometry/Unity acceptance.

### SCENARIO-01 — P1 · VERIFY · Draft ETag và snapshot

- **Contract/current:** Draft GET/PUT, ETag dựa trên xmin và snapshot/version đã có. Không cần tạo API GET draft mới.
- **Sửa code:** Giữ GET draft trả ETag; kiểm tra PUT `If-Match`, snapshot bất biến, liên kết version với đúng scenario/revision và review reject đúng cặp.
- **Nghiệm thu:** GET draft lấy ETag ban đầu; lưu với ETag hiện tại thành công; thiếu/cũ ETag bị từ chối; hai editor không ghi đè nhau; snapshot cũ không đổi.

### PLAYTEST-01 — P1 · SOURCE/HTTP/DB TESTED · Tenant, entitlement and launch

- **Contract/current:** OrganizationUser owner/session only; preparation pins accepted immutable package without entitlement/quota/grant; start checks current runtime + exact Building entitlement and consumes Trial atomically with receipt/audit. Separate five-minute grant and replay implemented. [Manual tests](playtest-manual-test.md).
- **Remaining acceptance:** Production configuration, real IFC/Unity/runtime client; no automatic Trial is provisioned. No playtest completion/sync/result API or learner analytics is claimed. Publish stays contained.

### RELEASE-01 — P1 · GAP · Publish gates

- **Contract/current:** Runtime dùng [FailClosedReleaseStore](../Fire3D/Fire3D.Infrastructure/Releases/FailClosedReleaseStore.cs): publish trả 503 thay vì bỏ qua gate. Create Built, GET và revoke vẫn delegate implementation hiện có.
- **Sửa code:** Tách ghi nhận build hoàn tất khỏi việc chạy Unity. Trước publish kiểm approval đúng scenario/rubric hash (APPROVAL-01), readiness đúng revision/version, package/manifest hash, artifact, validation Passed, blocker, runtime compatibility, Training và entitlement theo Docs.
- **Nghiệm thu:** Provenance/review sai, QA lỗi hoặc còn blocker, runtime không tương thích, entitlement không Active đều không publish. Retry/revoke/audit đúng; create Built không bị mô tả là đã chạy Unity.

### SESSION-01 — P1 · LATER · QR và training session

- **Contract/current:** QR Building, Training list, session preparation/start, launch grant và offline continuation/sync chưa có đủ API production.
- **Sửa code:** Sau ACCESS-01, APPROVAL-01, CAPACITY-01 và release/entitlement gates, triển khai QR cấp Building; list/package/prepare kiểm access, prepare pin release/scenario/package. Explicit online start recheck access/approved/published/entitlement/runtime, phân suất nguyên tử rồi cấp grant; pin entitlement/review/rubric.
- **Nghiệm thu:** Package cache không mở session mới offline; prepare/playtest không tính seat. Retry start không tạo session/seat trùng. Phiên đã start tiếp tục/sync sau expiry, rotate/revoke quyền hoặc account bị khóa; event/result vẫn kiểm owner và replay hash.

### ASSESSMENT-01 — P1 · LATER · Kết quả theo rubric (FR-TRAINING-01,08)

- **Contract/current:** Chưa có đầy đủ runtime/result API v7; session Completed không suy ra Passed.
- **Sửa code:** Lưu rubric hash đã duyệt, criterion results, score, outcome `Passed|NotPassed|Incomplete|NotAssessed` và lý do theo session/version. Trainee tự chọn mode, retry không giới hạn; AI bị chặn trong Assessment.
- **Nghiệm thu:** Kết quả cũ giữ rubric cũ sau publish mới; replay không ghi trùng; Completed không tự Passed; không prerequisite/module/sprint/certificate. Threshold/trọng số cần policy được chốt trước implementation.

## D. Billing, notification, AI, Learn và báo cáo

### BILLING-01 — P1 · CODE/TEST · PayOS và entitlement Building

- **✅ Có code:** Catalog/quotation Draft → Issued → Accepted; SDK payOS2.1.0, checkout idempotent, provider-confirmed cancel, verified durable inbox, ledger/provisioning atomic, cấp/gia hạn từng Building, lease/retry/reconcile, API trạng thái và entitlement. Trang return/cancel chỉ điều hướng. Contract/test tay tại [billing.md](billing.md).
- **✅ Kiểm chứng tự động:** SDK chữ ký offline, HTTP authorization/OpenAPI, PostgreSQL disposable cho replay/race/rollback/partial recovery, UTC month-end và grants executor. Kết quả cuối đợt ghi trong billing.md; không coi test mock là provider acceptance.
- **✅ Kiểm chứng local/provider một phần:** Sáu migration PayOS và login/grants đã áp vào Supabase cho test local; tạo link/QR provider thật và replay cùng operation đạt. Chưa chuyển tiền.
- **❌ Chưa nghiệm thu:** Runtime executor/worker deployment Azure, đăng ký webhook/probe PayOS, giao dịch thật với bank/provider và kiểm entitlement deployed. Không tick publish/playtest/training gate từ entitlement storage.
- **❌ Backlog riêng:** Reminder 5 ngày, revenue, package/capacity v7 và AI prepaid top-up/provisioning, hoàn tiền tự động, eInvoice và FE billing đầy đủ. Không lập AI invoice cuối kỳ hoặc overage debt.
- **Nghiệm thu:** Return URL không ghi Paid; amount/currency/link sai không được apply; webhook/recovery/replay không cấp trùng; cùng payment có mốc kích hoạt chung, renewal nối kỳ đã mua; Paid và provisioning riêng biệt.
### NOTIFY-01 — P1 · LATER · Nhắc hết hạn

- **Contract/current:** Mailgun hiện phục vụ reset; reminder dịch vụ chưa có. Docs yêu cầu web/email trước 5 ngày.
- **Sửa code:** Thêm scheduler/outbox/delivery idempotent theo entitlement, kỳ và channel; gia hạn phải vô hiệu reminder cho kỳ cũ.
- **Nghiệm thu:** Retry/duplicate không gửi trùng một kỳ/kênh; email và in-app notification cùng đúng entitlement; kiểm provider thật riêng với unit test.

### AI-01 — P1 · LATER · AI request và quota

- **Contract/current:** V7 dùng Organization quota pooled trả trước từ Building/package/top-up và Trainee daily quota riêng; chưa có luồng production BE request/quota đầy đủ. Không còn overage consent, AI billing period hoặc nợ trả sau.
- **Sửa code:** Durable request/result qua BE; reserve quota trước billable work, settle/release idempotent và reconcile timeout. Giữ policy/usage/grant provenance, lock order request → ledger/reservation → grants theo ID. Quota request không tạo invoice; BILLING-02 cấp quota qua payment đã áp dụng.
- **Nghiệm thu:** Concurrent reserve không vượt quota; thiếu quota chặn request mới; duplicate/hash conflict, late result/timeout/release không trừ trùng; quota Trainee không consume pool Organization. Provider nằm ngoài transaction DB.

### CAPACITY-01 — P1 · LATER · Hạn mức người theo kỳ (FR-BILLING-11)

- **Contract/current:** Chưa thấy learner-seat/upgrade v7 trong source.
- **Sửa code:** Một distinct Trainee user ID chiếm một seat tại Building/entitlement khi start lần đầu; nhiều bài/lượt cùng kỳ chỉ một seat. Tòa khác/kỳ renewal mới tính riêng. Upgrade giữ kỳ và số seat đã dùng; user đã tính được start tiếp khi access/service hợp lệ.
- **Nghiệm thu:** Hai user tranh suất cuối chỉ một thành công; retry cùng user không tăng count; login/list/prepare/playtest không tính. Upgrade không reset seats hoặc chồng kỳ cam kết.

### BILLING-02 — P1 · GAP · Gói v7 và AI top-up (FR-BILLING-01,05–07,11)

- **Contract/current:** PayOS/Building billing hiện có code/test theo delivery tháng 10; chưa biểu diễn đầy đủ package 6/12 tháng, learner limit, quota policy và top-up v7.
- **Sửa code:** Snapshot game service, seats, quota/price/terms cho từng Building line New/Renewal/Upgrade. Trước Issue, quota-bearing line pin tenant/audience/unit/policy version và grant interval hợp lệ. Provision payment/line idempotent; top-up cấp pooled AI grant riêng và không gia hạn Building. Kiểm replay đã provision trước điều kiện expiry; không chồng kỳ đã cam kết.
- **Nghiệm thu:** Replay sau expiry trả entitlement/grant đã cấp; webhook lặp không tăng quota/seats; top-up không đổi kỳ Building; invalid policy/interval chặn Issue. Giá/expiry/rollover/công thức upgrade phải cấu hình, không mặc định từ schema.

### RAG-01 — P1 · LATER · Learner-safe retrieval (FR-AI-03,05–06)

- **Contract/current:** V7 có `scenario_knowledge_documents`; chưa thấy index/retrieval authorization production tương ứng.
- **Sửa code:** Chỉ index snapshot approved/published đúng hash gồm `name`, `objectives`, `instructions`; mỗi retrieval recheck current access/service trước vector retrieval. Loại draft/rubric/đáp án/private IFC. Learn pin post/version hợp lệ, Hidden có thể RAG, Deleted/Unpublished bị loại.
- **Nghiệm thu:** Access/service mất quyền chặn Building retrieval mới dù index/cache còn; vẫn đọc giải thích kết quả của chính mình đã lưu và Common hợp lệ. Assessment chặn AI; nguồn/version khác tenant bị từ chối.

### LEARN-01 — P1 · LATER · CMS và bookmark

- **Contract/current:** Chưa có CMS/API/persistence Learn production. Docs quy định PlatformAdmin quản trị; không có bước approve riêng.
- **Sửa code:** Thêm post/version/situation/source/bookmark; public chỉ đọc Published; Hidden không public nhưng được phép RAG, Deleted bị loại; validate provider URL; audit/idempotency và cache invalidation qua outbox.
- **Nghiệm thu:** Draft không public; version Published bất biến; Hidden/Deleted không trả public, Deleted không vào RAG; bookmark chỉ thuộc Trainee; không thêm approve route ngoài contract.

### REPORT-01 — P2 · PARTIAL · Analytics/support/audit views

- **Contract/current:** Có feedback/ticket/message cho user và admin, audit metadata admin-only, cùng operations analytics cho platform/organization. Operations analytics không phải learner analytics; chỉ số plays, active sessions, completion/duration chưa có nguồn session chuẩn.
- **Còn thiếu:** Idempotency receipt/ETag cho support mutation, pagination/filter support và PostgreSQL integration tests cho tenant/race. Learner analytics chỉ được thêm sau start/heartbeat/result chuẩn.
- **Nghiệm thu:** Playtest/preparation không tính learner play; dashboard/API dùng chung định nghĩa; người dùng không đọc tenant khác; dữ liệu nhạy cảm được che.

## E. Thứ tự phụ thuộc và cách hoàn tất task

1. **DB-01, AUTHZ-01, AUTH-01–04:** schema test, tenant boundary, đăng ký/onboarding/profile/session/recovery.
2. **IFC-01, IFC-02, SCENARIO-01, LIBRARY-01, APPROVAL-01:** worker/QA, draft/version, thư viện và approval; readiness và approval là hai dependency riêng.
3. **BILLING-01–02, ACCESS-01, CAPACITY-01, NOTIFY-01, PLAYTEST-01, RELEASE-01:** payment/provisioning, access/capacity và publish gates.
4. **SESSION-01, ASSESSMENT-01:** start/launch grant, pin rubric, offline sync và kết quả theo mode.
5. **AI-01, LEARN-01, RAG-01, REPORT-01:** request/quota và retrieval đã cấp quyền; indexing cần approved/published version, analytics cần session/result chuẩn.

Task chỉ hoàn tất khi contract, handler/store/schema/gate và role/tenant đúng; có kiểm tra happy path cùng lỗi/race/replay phù hợp trên database test; tài liệu API phản ánh source mới; và PR ghi lệnh, kết quả, phần bị mock/bỏ qua, cùng giới hạn provider. Không coi build, route tồn tại hoặc mock test là bằng chứng provider/production đã hoạt động. Đợt đồng bộ này chỉ cập nhật BE docs; không sửa bộ `Docs` chuẩn.

Google onboarding/link: source/DI/controller + PostgreSQL migration/constraints/RLS, atomic create/replay/race/rollback đã có kiểm thử; Firebase thật, client và deployment chưa kiểm chứng. Migration chưa áp Supabase. Hướng dẫn: [google-auth-manual-test.md](google-auth-manual-test.md).

## Building mutation — code và test trong đợt API được chọn

POST body `organizationId`/alias query, admin PUT/DELETE derive tenant, validation nested theo field đã triển khai. Unit/HTTP và PostgreSQL chạy migration history thật, gồm runtime audit INSERT-only, rollback audit và race khóa lifecycle được ghi trong `building-manual-test.md`. Access/participation, IFC worker, readiness/approval, playtest và package build vẫn là các task riêng; không suy hoàn tất từ Building CRUD. Supabase/deployment chưa cập nhật trong đợt này.

### Selected scope Task 2 — bound IFC upload (source/test; deployment pending)

Hai route initiate dùng chung intent/receipt và SHA-256 dự kiến. Complete kiểm owner/tenant/key/size/hash thực, lưu candidate trước copy, adopt atomic; cleanup có lease/retry và tombstone để xử lý late write. Migration additive `AddBoundIfcUploads` giữ source legacy unverified. Xem `ifc-upload-manual-test.md`. Test PostgreSQL isolated + HTTP/storage fake; không đánh dấu pipeline IFC/Blender/Unity hoặc Supabase/S3 production hoàn tất.

### Selected scope Task 3 — processing/outbox/HTTP worker (source/test; provider pending)

Process dùng verified source + Idempotency-Key và SQL gate atomic. Machine-only claim/renew/output/complete/fail, restricted executor, dispatcher receipt-before-ACK, lease fencing/recovery và retry gate đã triển khai. Artifacts/QA/issues chỉ lấy current accepted attempt; alias additive giữ các cột legacy. Xem `processing-worker.md`. Worker nghiệm thu bằng fake HTTP, chưa có IFC/Blender/Unity thật; dispatcher mặc định tắt. Readiness/approval/package runtime/playtest và publish chưa được suy hoàn tất từ kết quả này.


Scenario authoring: implemented durable create/snapshot/package-build receipts, draft ETag 428/400/412, locked numbering, immutable canonical v7 snapshot/rubric/learner fields and accepted geometry/runtime references. PostgreSQL actual-history plus fake package output tests ran; real Unity and deployment remain unchecked. See scenario-authoring.md. Structural validation alone is not readiness.

### Selected scope Task 5 — exact readiness and content approval

Source + PostgreSQL/HTTP tests implemented: exact revision/version/run/annotation/artifact provenance, Passed/blocker/runtime gate, separate immutable Submitted→Approved/Rejected content review, server hashes, live tenant/lifecycle, receipt and audit rollback. See [scenario-readiness.md](scenario-readiness.md). Tests use fake worker outputs, not Unity. Publish/learner sessions remain incomplete. Supabase Tasks 2–4 plus dependency EXECUTE repair were applied 2026-10-07; existing row counts were preserved. Older "deployment pending" notes describe their original implementation evidence, not the subsequent schema rollout.

Task 6: isolated actual-history PostgreSQL + HTTP/OpenAPI + fake runtime/package/paid-provider fixture verified; migrations are additive and preserve legacy sessions. Tasks7–9 now have source/test/docs: release/Training/access, support receipts/ETag/paging and OpenAPI inventory. Their two new migrations await shared deployment.


### Schema rollout 2026-10-07

Supabase migrations through `20261006160000_AddPlaytestLifecycle` have been applied and postchecked; see [ifc-authoring-deployment.md](ifc-authoring-deployment.md). Existing 8 users/6 organizations/2 buildings were preserved. Earlier deployment-pending notes are historical evidence for their original task commits. Readiness/playtest source and isolated tests are verified; matching deployed API binary, production worker and runtime client remain unchecked. Tasks7–9 source/isolated tests are recorded in [selected-api-contract.md](selected-api-contract.md); new release/access and support migrations are not applied to Supabase. Real publish/learner flows remain incomplete.

## Selected scope Tasks7–9

[Current route checklist and evidence](selected-api-contract.md) replaces historical selected-API gap descriptions. [Generated route inventory](api-route-inventory.md) counts OpenAPI methods, not completed capabilities. Built/access/support are source/test verified; production migration/binary/client and real workers are separate. Publish, learner start/sync/result remain incomplete.
