# Danh sách sửa BE theo contract trong Docs

Tài liệu này là backlog đối chiếu code BE với chuẩn nghiệp vụ trong workspace [`Docs`](../../Docs/README.md). Contract gốc nằm ở [requirements](../../Docs/fire_evacuation_requirements.md), [workflows](../../Docs/fire-evacuation-training-workflows.md), [technology](../../Docs/fire-evacuation-training-technology.md), [schema SQL](../../Docs/fire_evacuation_schema.sql) và [ERD](../../Docs/fire_evacuation_erd.md). API guide mô tả hành vi source đang có; checklist này ghi phần còn phải sửa. Không coi tên entity, route hay thiết kế SQL là bằng chứng tính năng đã hoàn tất.

## Tóm tắt trạng thái 12/10/2026

- Mọi nhóm API trong backlog này đã có source và test HTTP/PostgreSQL (restricted runtime role, tenant isolation), trừ RAG-01.
- **Ngoài phạm vi hiện tại:** RAG-01 / AI trả lời cho Trainee (`POST /api/ai/trainee/answer`) và adapter AI provider thật; không triển khai trong đợt này.
- **Còn nghiệm thu provider/deployment, không phải thiếu API:** S3, Firebase, Mailgun, FCM, PayOS giao dịch thật, worker IFC/Unity/Mobile trên Azure.
- Supabase đã áp toàn bộ migration tới `20261012200000_AddTrainingReleaseList` (85 migration) ngày 12/10/2026.
- Nhãn P0/P1/GAP/VERIFY/LATER bên dưới là baseline 04/10; dòng "Trạng thái 12/10/2026" ở từng mục là trạng thái hiện hành.

- **14/10/2026 (`fix/p0-authz-auth-gaps`):** DB-01/AUTHZ-01/AUTH-01/04/05 được rà lại trên `main` `1eaf988`; lỗi và test bổ sung ghi ở dòng "Trạng thái 14/10/2026" từng mục. Supabase đã áp tới `20261014090000` (kiểm tra đọc ngày 14/10: grant thu hẹp, bảng recovery, RLS đúng); `20261014100000`/`20261014100100` chưa áp, phải áp cùng lúc deploy API.

## Kiểm chứng deployment bổ sung ngày 03/10/2026

Các trạng thái lịch sử bên dưới chỉ có giá trị trong phạm vi baseline ghi kèm; bằng chứng mới về PayOS/deployment nằm tại [payos-deployment.md](payos-deployment.md).

- ✅ Supabase đã nhận sáu migration PayOS, migration normalized email và login/grants giới hạn quyền cho test local; không reset dữ liệu.
- ✅ API local tạo checkout/QR PayOS thật 100.000 VND, create201/replay200 cùng checkout/order; chưa chuyển tiền.
- ✅ Azure Swagger/OpenAPI/health/return/cancel200; preflight OTP và PayOS cho cả hai domain FE204, có CORS đúng. Create thiếu Bearer401, webhook thiếu chữ ký400, OTP/resend email sai400 có lỗi theo field.
- ❌ Chưa nghiệm thu worker/executor trên Azure, gửi OTP thật, webhook hợp lệ, Paid/Applied và provisioning/entitlement thật.
- Finding Swagger tại baseline deployment 03/10: multipart/PATCH/enum/proof/header sai hoặc thiếu. Phần **auth** đã sửa source bằng schema/operation transformer và có contract test ngày 05/10; binary/OpenAPI deployment mới chưa kiểm chứng. Ghi chú này thuộc đợt auth; GET PayOS response200 và metadata API được chọn đã có bản sửa/test trong đợt Tasks1–9 bên dưới.

## Cập nhật auth source/test — 05/10/2026

[Checklist 28 endpoint auth](auth-api-checklist.md) ghi riêng code/test/provider. Avatar đã chặn lease hết hạn và có PostgreSQL recovery/rollback/race; OpenAPI auth khai báo đúng schema PATCH, multipart file, proof bắt buộc, enum tên, If-Match và installation proof. Google source đã có onboarding/link; chưa áp migration mới lên Supabase hoặc test Firebase/S3 thật. Không dùng tình trạng deployment lịch sử để đánh dấu binary mới đã nghiệm thu.

## Baseline và trạng thái

Redis processing bổ sung ngày07/10/2026: có source và Docker tests cho transport IFC/package-build tùy chọn, publisher/bridge/recovery/retention; Http giữ mặc định. PostgreSQL vẫn lưu job/outbox/receipt. Ba migration Redis đã áp Supabase với kiểm tra quyền/dữ liệu; dispatcher login/binary chưa triển khai. Azure dev TCP/TLS/key/Streams smoke pass sau khi người dùng bật public network access; policyvolatile-lru, HA/AOF và worker/toolchain thật chưa nghiệm thu. Không thêm cache/auth rate limit/Hangfire. [Cấu hình và rollout](redis-processing.md), [checklist riêng](task-progress-checklist.md#redis-processing--phạm-vi-bổ-sung-07102026).

Organization phone uniqueness có source cho email/Google/PATCH: `409 ORGANIZATION_PHONE_EXISTS`, field error đúng DTO; [preflight chỉ đọc](../database/006_organization_phone_preflight.sql) và migration additive chặn duplicate canonical trên mọi tổ chức, không sửa phone/revision legacy. PostgreSQL disposable kiểm cross-flow, race, proof rollback/retry, lifecycle, runtime role và migration history. Supabase đã áp migration/index ngày06/10/2026; binary deployment/FE chưa nghiệm thu. [Kịch bản test tay và bằng chứng DB](organization-phone-manual-test.md). Cá nhân có index riêng ngày07/10/2026: `PHONE_NUMBER_EXISTS/errors.phoneNumber`, không uniqueness chéo với tổ chức; [contract/test/rollout](personal-phone-uniqueness.md).

Đồng bộ contract ngày 04/10/2026 theo [Docs v7](../../Docs/schema_v7_contract.md), Docs commit `2b56ba3`; đối chiếu source BE `origin/develop` tại `e42a2eb`. Các kết quả test/deployment ngày 02–03/10 giữ baseline và giới hạn riêng. Trước khi triển khai mỗi work item phải xác nhận lại source; lần rà tài liệu này không chạy lại application tests hoặc provider.

- **GAP:** capability/contract còn thiếu hoặc source đang lệch.
- **VERIFY:** đã thấy code cho một phần contract; cần kiểm thử và/hoặc khớp schema/provider trước khi kết luận.
- **LATER:** requirement có trong Docs, triển khai sau dependency nêu trong thứ tự công việc.
- **PARTIAL / CODE/TEST:** source có một phần hoặc có bằng chứng kiểm thử lịch sử; không đồng nghĩa đã triển khai đầy đủ contract v7.
- Mỗi task triển khai cập nhật checklist, API guide và bằng chứng trong PR. Chỉ cập nhật trạng thái sau khi kiểm cả quyền, tenant, trạng thái, persistence, audit/idempotency và lỗi liên quan.

## Publish and scenario review issue acceptance

| Contract | Source | Automated checks | Deployment |
|---|---|---|---|
| Publish gated by paid service, approval, exact readiness, QA, accepted package/runtime and active Training | Implemented; rollout defaults off | PostgreSQL/HTTP regression; current result recorded in handoff/PR | Requires new migrations, matching binary and explicit flag enablement |
| Idempotency-Key,200 Published DTO, GET/revoke/current Training visibility | Implemented; receipt replay never republishes | Receipt/conflict/race/audit rollback and HTTP readback | Azure/real Unity acceptance separate |
| Admin review queue/detail with filters/names/content/rubric/readiness | Implemented | HTTP and restricted runtime SQL tests | New read-gate migration required |
| Organization version detail/list reviewStatus/reviewId/rejectReason | Implemented | Reload, role/session and tenant isolation tests | FE/binary integration separate |
| Draft/release/readiness lists and typed editor schemas from additional issues | Implemented (#53, #54, #66) | HTTP, restricted runtime SQL and tenant isolation tests | New read-gate migrations required |

[Exact contracts, error codes and manual tests](publish-review-manual-test.md). Do not infer deployment from source or mock worker results.

## A. Database và nền tảng dùng chung

### DB-02 — P0/P1 · CODE/TEST · Backend ACL, IFC enqueue và token cleanup

- **Đã sửa source:** Migration forward-only `HardenBackendObjectPermissions` chặn explicit Supabase anon/authenticated grants trên 6 bảng backend và PayOS SECURITY DEFINER entrypoints; giữ grant executor, thêm backend RLS và sửa default ACL của migration owner. `AddIfcIntegrationOutbox` giao canonical tenant enqueue; `AddRefreshCleanupGate` cho API dọn family hết retention mà không có direct DELETE. [Hardening](database-hardening.md), [outbox](ifc-outbox.md), [cleanup](refresh-token-cleanup.md).
- **Kiểm chứng:** PostgreSQL cô lập có test quyền backend/executor, rollback/replay outbox, retention và lock recheck cleanup. Full-chain Auth fixture và non-superuser owner-transfer có kiểm tra riêng. Schema này không phải bootstrap toàn bộ v7.
- **DB đã kiểm chứng:** 05/10/2026 áp đúng 3 migration lên Supabase hiện có, history 29→32; giữ 4 account/16 refresh token, executor grants và kiểm read-only bằng API identity giới hạn quyền. [Bằng chứng và giới hạn](database-hardening.md#deployment-evidence--2026-10-05). **Còn phải nghiệm thu:** Azure binary, latency thực tế, worker IFC/provider thật; DB update không chứng minh các phần này hoạt động. Quyền API không chuyển thành postgres.

### AUTH-PERF — CODE/TEST · Đo latency và giảm round trip

- Password verify/rehash ngoài lock, snapshot email/hash và lifecycle được kiểm lại dưới lock; login/session/audit và reset fence atomic. Middleware session authorization còn một SQL query, vẫn kiểm quyền live và family.
- [Metric và giới hạn](auth-performance.md). Test không chứng minh p95 login Azure đã giảm; không thêm index trùng, giảm hash strength hay cache quyền. Google onboarding/link có source và regression, AUTH-02 chưa nghiệm thu Firebase/client/deployment.

### DB-01 — RÀ SOÁT CHUNG · Schema mapping

- **Trạng thái 12/10/2026:** không còn API thiếu ở mục này. Test chạy trên lịch sử migration thật (không `EnsureCreated`) với restricted runtime role; Supabase đồng bộ tới `20261012200000`. Mapping v7 đầy đủ (bảng legacy `password_reset_tokens`) vẫn là việc dọn dẹp riêng, không chặn API. Nội dung dưới đây là baseline.

- **Trạng thái 14/10/2026:** `local_password_reset_tokens`, `password_reset_email_jobs`, `password_reset_operations` trước đây chỉ do script tay `database/002`/`003` tạo; database dựng từ migration bị lỗi forgot/reset/change/Google link. Migration `20261014090000_AddPasswordRecoveryTables` tạo idempotent, bật RLS và grant runtime; fixture HTTP không còn chạy script tay, có test forgot → reset → change trên DB chỉ từ migration (cả restricted runtime login). `20261013100000_NarrowRuntimeWriteGrants` thu UPDATE toàn bảng cấp tay trên `users`/`organizations`/`auth_refresh_tokens`. Migration `20261014100000_AlignAuthSchemaWithV7` đưa bảng định danh về v7 không xóa dòng: đổi `profile_revision` → `profile_revision_no` (giữ dữ liệu/quyền cột), thêm CHECK v7 trên `users`/`organizations` (chỉ khi không có dòng vi phạm, nếu có thì NOTICE), xóa `users.avatar_url` và `organizations.plan` (không còn code đọc; dừng migration nếu còn dữ liệu khác NULL/`free`), `user_devices` thêm `fcm_token_updated_at`, `fcm_token` kiểu text, index `device_active_fcm`, FK RESTRICT. Supabase 14/10 (đọc, đếm): 0 dòng vi phạm CHECK, 0 `avatar_url`, mọi `plan`=`free`. Phải áp cùng lúc deploy API mới vì đổi tên cột. Giữ khác v7 có chủ đích: `organizations.registration_owner_user_id` (dọn đăng ký dở cần), `user_devices.device_uuid` unique theo user vì `sessions` khóa ngoại cặp (device, trainee); unique toàn hệ thống sẽ buộc nới FK đó.

- **Contract/current:** SQL v7 là đích; EF/migration/runtime chưa đồng bộ toàn bộ. `LocalPasswordReset` dùng `local_password_reset_tokens`, đã được v7 biểu diễn; EF vẫn giữ `password_reset_tokens` legacy. Chưa được suy môi trường hiện tại trống từ nhận định tháng 09; xem bằng chứng Supabase/PayOS phía trên.
- **Sửa code:** Lập mapping entity/enum/query/grant sang v7, kiểm tra schema/dữ liệu từng môi trường và đường gọi reset trước khi xử lý bảng legacy. Dựng database test mới theo schema đích; không dùng `EnsureCreated` thay SQL/gates. Schema v7 đã syntax-load trên PostgreSQL disposable với stand-in pgvector theo contract Docs; cần kiểm actual extension/quyền/runtime riêng.
- **Nghiệm thu:** Database test mới dựng được từ schema mục tiêu; enum/FK/constraint/grant và truy vấn task chạy đúng. Migration/backfill của môi trường khác phải dựa trên dữ liệu đã kiểm tra; không chạy destructive SQL lên DB dùng chung để kiểm chứng tài liệu.

### AUTHZ-01 — RÀ SOÁT CHUNG · Actor và tenant

- **Trạng thái 12/10/2026:** các gate SECURITY DEFINER kiểm actor/role/live session/tenant/lifecycle trong DB và có test tenant khác/role sai qua #46–#69. `Guid.Empty` còn lại trong controller chỉ là resource placeholder cho list/create, không tắt tenant check. Nội dung dưới đây là baseline.

- **Trạng thái 14/10/2026:** rà lại không thấy đọc/ghi chéo tenant trên route đang dùng (JWT được đối chiếu role/tenant/family với DB mỗi request). Đã sửa: `PUT /api/buildings/{id}` từ chối Building archive (404, kiểm lại dưới khóa); xóa initiate/finalize/confirm không còn được gọi trong `IfcWriteStore` (bỏ qua tenant khi null); `ProcessRevisionAsync` và `FailClosedPlaytestWriteStore` không còn coi tenant null/rỗng là mọi tenant. Có HTTP test GET/PUT/DELETE/list Building tenant khác và luật archive. Release đọc của Building archive vẫn hiển thị trong tenant (lịch sử), chờ quyết định sản phẩm.

- **Contract/current:** Mọi thao tác phải kiểm actor, role, tenant và trạng thái resource ở BE. Một số luồng dùng `Guid.Empty` làm scope rộng hoặc dựa vào tenant claim mà chưa xác minh quan hệ resource.
- **Sửa code:** Rà controller, handler và store; lấy actor từ phiên đã xác thực, xác minh tenant qua quan hệ trong DB và áp dụng permission matrix của Docs. Bỏ fallback ID rỗng có thể tắt tenant check.
- **Nghiệm thu:** Tenant đúng được phép; tenant khác, Trainee không đủ quyền hoặc account/organization/Building không hoạt động bị từ chối. Lỗi không làm lộ resource ngoài scope; audit có đúng actor.

## B. Tài khoản và xác thực

### AUTH-01 — CODE/TEST · Đăng ký local

- **Trạng thái 12/10/2026:** OrganizationUser nhận `username` tùy chọn khi đăng ký (cùng luật Trainee, trùng 409 USERNAME_EXISTS, lỗi không consume proof). Còn nghiệm thu FE/inbox OTP trên bản deploy. Dòng "Còn thiếu" bên dưới là baseline.

- **Trạng thái 14/10/2026:** có HTTP/PostgreSQL test hai email đăng ký đồng thời cùng username qua route Trainee và Organization: một 201, một 409 USERNAME_EXISTS, không tạo dở account/organization, proof bên thua vẫn dùng được.

- **Contract/current:** FE nhập form trước → OTP → một nút gọi verify-otp rồi register với toàn bộ form + proof. `POST /api/auth/registration/request-otp` tạo challenge/job gửi mã nhưng không tạo identity; `POST /api/auth/resend-verification` gửi OTP mới sau cooldown và vô hiệu mã/proof cũ. Email đã có account trả 409 EMAIL_EXISTS + errors.email, không enqueue. Ba route register consume proof trong transaction tạo role/tenant server-owned và audit; form sai không consume proof. `/check-email/` là demo OTP, FE thật cần tích hợp riêng. `/verify-email` chỉ dành link pending legacy. Migration `20261003030000_AddNormalizedRegistrationEmail` bổ sung unique email trim/lowercase và guard legacy duplicates, không xóa/gộp dữ liệu; migration OTP/username cũ giữ nguyên. `/api/auth/register` là alias Trainee. [Test tay](registration-payos-manual-test.md); mức nghiệm thu DB/provider/FE phải ghi riêng.
- **Còn thiếu:** username cá nhân tùy chọn cho đăng ký OrganizationUser. PATCH profile tổ chức đã có ở `OrganizationProfileController`; email verification, profile ETag, đổi username và avatar cần nghiệm thu provider/deployment riêng.
- **Nghiệm thu cần chạy:** HTTP/PostgreSQL disposable kiểm tra proof không tạo user trước verify, proof chỉ dùng một lần, hai role, organization transaction và collision username khác hoa/thường. Còn cần test race concurrent cùng username và chuyển dữ liệu production trước khi đóng hoàn toàn.

### AUTH-02 — CODE/TEST · Google onboarding và link; deployment chưa nghiệm thu

- **Contract/current:** [ExchangeFirebaseTokenCommand](../Fire3D/Fire3D.Application/Authentication/Commands/FirebaseLogin/ExchangeFirebaseTokenCommand.cs) xác minh Google và trả `OnboardingRequired` cho identity mới; đã có proof15 phút và `/api/auth/google/onboarding/complete`, tạo Google-only Trainee/OrganizationUser + receipt/login/refresh-hash/Create+Login audit atomic, trả201 Authenticated. Nested proof có verified email/displayName và alias deprecated. Replay cùng input24 giờ trả409 ONBOARDING_ALREADY_COMPLETED → exchange recovery, không cấp thêm phiên. Invalid/expired split400; quota/lock timeout có Retry-After. `/api/me/link-google` yêu cầu live family/local password/verified Google; link mới revoke sessions/reset proofs và tăng profile revision + audit atomic, giữ role/tenant/email, cùng UID replay với session mới không duplicate audit.
- **Exchange đã củng cố:** [FirebaseIdentityProvider](../Fire3D/Fire3D.Infrastructure/Authentication/FirebaseIdentityProvider.cs) dùng SDK/revocation, Google provider và email verified, timeout15 giây;401 invalid khác503 unavailable. Recheck UID owner/lifecycle dưới khóa, giữ response Authenticated/OnboardingRequired/ACCOUNT_LINK_REQUIRED. [FirebaseGoogleVerificationTests](../Fire3D/Fire3D.AuthTests/FirebaseGoogleVerificationTests.cs) kiểm claims/revoked/timeout/cancellation; [GoogleExchangeHardeningTests](../Fire3D/Fire3D.AuthTests/GoogleExchangeHardeningTests.cs) kiểm ownership/lifecycle và không auto-link. Test SDK giả lập, không xác nhận Firebase production.
- **Đã triển khai:** proof15 phút và replay24 giờ, Google-only Trainee/OrganizationUser; link explicit không auto-match email, không replacement/chiếm UID; lock lifecycle → UID → user.
- **Nghiệm thu:** Kiểm tra UID mới/đã link, email local chưa link, token hết hạn, retry cùng/khác input và hai request đồng thời. Không tạo user/organization trùng hoặc đổi role/tenant tài khoản đã link.

### AUTH-03 — CODE/TEST · Profile, ETag và avatar

- **Trạng thái 12/10/2026:** API profile/ETag/avatar đủ; AccountResponse có thêm `hasLocalLogin`/`googleLinked`. Chỉ còn nghiệm thu S3/IAM/CORS thật.

- **Contract/current:** `GET/PATCH /api/auth/me` trả/nhận ETag theo `profile_revision`; PATCH hỗ trợ full name, username, dob, gender và phone. `/api/me/avatar` có intent/complete/GET/delete, complete copy theo S3 ETag và không dùng chung final key giữa các request. `GET/PATCH /api/organizations/me` trả ETag và PATCH chỉ thay đổi field được gửi.
- **✅ Code/test Avatar:** Candidate được lưu trước conditional copy; reserve/finalize chặn lease hết hạn theo clock DB sau khóa. Có PostgreSQL disposable test restricted role/RLS, rollback audit, cleanup/retry/protection, complete/delete cạnh tranh; SDK signing offline và stream S3 giả lập có test. [Test tay và giới hạn](avatar-manual-test.md).
- **❌ Chưa nghiệm thu:** S3 thật/IAM/CORS, request treo vượt cleanup grace, FE mở signed URL và vận hành deployed worker. Không lập lại task bổ sung candidate path đã có; không suy IAM/object tồn tại từ URL ký được.
- **Nghiệm thu:** Lưu thành công trả ETag mới; ETag cũ không ghi đè thay đổi; không sửa được role/email/tenant/status; username, organization scope và quyền sở hữu object S3 được kiểm tra.

### AUTH-04 — P1 · CODE/TEST · Reset và change password

- **Trạng thái 14/10/2026:** bảng token/job/operation đã thuộc migration (xem DB-01). Có test change-password rollback hash và revoke session khi audit lỗi, hai change đồng thời chỉ một thắng. Chưa nghiệm thu Mailgun thật.

- **Contract/current:** Forgot/reset gửi qua worker Mailgun; change xác minh mật khẩu cũ và live family lấy từ JWT dưới lifecycle/user lock. Reset/change consume/invalidate token local và legacy, đổi hash, revoke session và audit atomic. Migration `AddPasswordRecoveryGate` cấp EXECUTE gate legacy cho backend, giữ lịch sử và không cần direct DELETE. Forgot/link/reset kiểm lifecycle; Google-only không được thêm password qua reset. Lookup email dùng trim/lowercase tương thích index/dữ liệu legacy.
- **Sửa code:** Giữ reset và change là hai luồng riêng. Hoàn tất mapping sang schema đích; kiểm tra rollback, race login/reset và mọi refresh-token family. Không coi có worker là bằng chứng Mailgun production đã gửi thành công.
- **Nghiệm thu:** Có PostgreSQL disposable regression cho restricted runtime role, reset replay/race/audit rollback, organization bị khóa, email legacy và change chờ khóa/family revoked. Migration database dùng chung và Mailgun/deployment thật cần nghiệm thu riêng; không đóng provider từ DB test.

### AUTH-05 — CODE/TEST · FCM device token

- **Trạng thái 12/10/2026:** `PUT/DELETE /api/auth/devices` có installation proof, family binding, rotate/revoke và test. Chỉ còn nghiệm thu FCM thật và lưu installation secret trên client.

- **Trạng thái 14/10/2026:** sửa unique violation FCM bị bọc trong `DbUpdateException` trả 500 thay vì 409 `FCM_TOKEN_ALREADY_BOUND`. Có HTTP/PostgreSQL test rotate (tăng generation), một token chỉ active trên một installation, sai installation key 403, user khác DELETE không ảnh hưởng, installation chuyển account vô hiệu binding cũ, logout-all tắt push. `INotificationService.SendPushToUserAsync` chưa có nơi gọi.

- **Contract/current:** AuthController có đăng ký/xóa device token và adapter FCM; v7 biểu diễn `device_installations` và binding user/session riêng. Code proof/family binding đã có, cần đối chiếu persistence/grants.
- **Sửa code:** Rà ownership theo user/installation, rotate/revoke, validate token và che token khỏi log. FCM chỉ dùng push, không dùng để đăng nhập.
- **Nghiệm thu:** Nhiều thiết bị, token invalid/replaced và user khác xóa token đều được xử lý đúng. Unit/mock không được ghi là bằng chứng FCM thật.

## C. Building, IFC, authoring và release

### ACCESS-01 — SOURCE/HTTP/DB TESTED · Quyền tham gia Building (FR-TRAINING-04)

- **Contract/current:** Building mặc định Private; visibility/access revision/code hash và account-bound participation grant đã có. Rotate/revoke/code hoặc visibility change vô hiệu grant cũ; Public vẫn yêu cầu đăng nhập. Training list Trainee kiểm current Building access và Active/Published/pinned approval. Không tạo tenant membership hoặc learner seat.
- **Kiểm chứng:** HTTP/PostgreSQL với runtime gate-only roles kiểm code sai, grant user khác, rotation/revoke, tenant và atomic audit. Supabase schema/ACL đã áp; binary/client chưa smoke test.
- **Còn chờ:** Learner package/preparation/start recheck và sync theo started snapshot thuộc SESSION-01; list/grant không chứng minh pipeline này đã có.

### LIBRARY-01 — SOURCE/HTTP/DB TESTED · Thư viện Organization (FR-LIBRARY-01–02)

- **Trạng thái 12/10/2026:** đã có `/api/library/*` (OrganizationUser/PlatformAdmin đọc) và `/api/admin/library/*` (PlatformAdmin quản trị) với version hóa template/rubric/thiết bị, publish bất biến và Idempotency-Key cho mutation (#57). Capability thiết bị chưa đối chiếu Unity. Nội dung dưới đây là lịch sử.

- **Contract/current:** Chưa thấy module library v7 trong source; catalog scenario cũ không chứng minh thư viện đã có.
- **Sửa code:** PlatformAdmin maintain template/rubric mẫu/metadata thiết bị có version; OrganizationUser đọc trong console để author. Template tùy chọn, capability chỉ chọn runtime hỗ trợ; tách Learn và kho riêng tenant.
- **Nghiệm thu:** Organization không quản trị library; sửa mẫu không sửa bài đã phát hành; private IFC/scenario không tự vào thư viện; capability không hỗ trợ bị từ chối.

### APPROVAL-01 — P1 · SOURCE/DB TESTED · Duyệt scenario/rubric (FR-SCENARIO-03)

- **Contract/current:** Exact immutable scenario/rubric submission and Admin approve/reject implemented with receipt, audit and live tenant/lifecycle checks. Technical readiness is independent. See [scenario-readiness.md](scenario-readiness.md).
- **Remaining acceptance:** Real client/deployment and publish feature rollout. Built release now checks the exact approval/readiness/package provenance; legacy versions receive no fabricated approval.

### IFC-01 — SOURCE/HTTP/DB TESTED · Bound source/outbox/worker contract

Verified immutable intent/source, canonical transactional outbox, HTTP dispatcher, machine-authenticated leased/fenced worker gates, accepted provenance, retry and recovery implemented and tested on isolated migration history with fake S3/HTTP worker. Supabase schema through Task6 applied; no real IFC/Blender/Unity output/deployed worker certification. See [processing-worker.md](processing-worker.md), [selected checklist](selected-api-contract.md).

### IFC-02 — P1 · SOURCE/DB TESTED · Readiness theo revision/version

- **Contract/current:** Confirm requires exact scenarioVersionId/validationRunId/annotationSetId, accepted current worker attempt/artifact, matching hashes, Passed/runtime ready and no Error/Critical blocker. Returns reviewId, atomic receipt/audit; technical rejection preserves history.
- **Remaining acceptance:** Built release consumes that exact confirmation and matching immutable approval/provenance. Fake worker evidence is not geometry/Unity acceptance.

### IFC-03 — P1 · GAP · Worker IFC: hình học, phân loại và QA

- **Contract/current:** Đề xuất trong [IFC ingestion, hiển thị và tương tác](../../Docs/ifc_ingestion_visual_interaction.md) mục 5.1–5.3, 5.9 (Docs PR #15/#16, spike 32 file IFC). Source BE chưa có các bước này; `tools/ifc-spike` mới parse hình học cơ bản.
- **Sửa code:** Chọn representation `Body` theo `RepresentationIdentifier` (20/25 file có `Axis` đứng trước; KIT đặt Body trong context `Design`); dựng lại phần tử có mesh rỗng; đọc type object qua `IfcRelDefinesByType` cho IFC2X3; đọc `IfcSpace.Name` + `LongName` với từ điển đa ngôn ngữ; không tự coi `IfcAlarm`/`IfcSensor` là báo cháy; ghi issue theo mã mục 5.9 (`IFC_NO_BODY_GEOMETRY`, `IFC_NO_SPACES`, `IFC_NOT_ARCHITECTURAL`, `IFC_NO_FLOOR_UNDER_SPACE`, `IFC_EXIT_UNKNOWN`, `IFC_UNSUPPORTED_FORMAT`…). Upload chỉ nhận `.ifc`; mô hình nhiều file bộ môn (mục 4) chờ chốt.
- **Nghiệm thu:** Theo fixture/tiêu chí mục 2.1 và 10: KIT dựng đủ 119 tường; trường học Revit tìm 6 sprinkler và 11 thiết bị vệ sinh; file không có phòng phát `IFC_NO_SPACES`; file chỉ kết cấu bị chặn. File IFC bên thứ ba giữ ngoài repo.

### IFC-04 — P1 · GAP · Thiết bị phát hiện và xác nhận

- **Contract/current:** Quy tắc đã chốt: IFC có thiết bị PCCC/nguồn nước thì pipeline tạo ứng viên; không có thì OrganizationUser đặt trong editor. Chưa có API/persistence.
- **Sửa code:** API đọc ứng viên (`catalogKey`, `confidence`, `source=ifc|editor`, `ifcGlobalId`, vị trí/bbox) theo revision; OrganizationUser xác nhận, đổi loại hoặc bỏ, có audit/ETag. Mô hình 3D luôn lấy từ thư viện asset, không từ IFC.
- **Nghiệm thu:** Tenant sai bị từ chối; xác nhận không sửa artifact revision; revision mới không kế thừa xác nhận cũ nếu `ifcGlobalId` không còn.

### SCENARIO-02 — P1 · GAP · Cấu hình tương tác và sprinkler trong ScenarioVersion

- **Contract/current:** Mục 6–7 của tài liệu trên: catalog đợt 1 (có sprinkler), luật tương tác theo ngữ cảnh, tham số tác dụng từ nội dung đã duyệt.
- **Sửa code:** Không thêm trường mới: dùng object `Equipment`/`Hazard` của contract `fet3d.editor/1` (`contracts/editor/v1`), coi `catalogKey` trong Docs là capability `{id,version}` của `GET /api/scenario-interactions/catalog`; bổ sung capability đợt 1 (mục 6.1, sprinkler mục 6.3) vào catalog và validate theo Organization Library/runtime; tham số tác dụng (khăn ướt, sprinkler…) không có giá trị mặc định ngầm; sprinkler có trạng thái `Active|Disabled|Faulty`; event mới (`alarm_raised`, `used_elevator`, `sprinkler_activated`…) được chấp nhận ở session event gate theo schema version.
- **Nghiệm thu:** `catalogKey` không có trong catalog hoặc vượt capability bị từ chối; snapshot version bất biến; event lạ bị từ chối rõ ràng, không làm hỏng sync.

### STYLE-01 — P2 · LATER · Preset vật liệu theo revision

- **Contract/current:** Mục 5.5: IFC hầu như không mang màu/texture dùng được; vật liệu lấy từ thư viện PBR CC0 theo preset công trình và loại phòng.
- **Sửa code:** Lưu preset phong cách và override vật liệu theo nhóm cho revision; worker Blender/Unity đọc và áp dụng tự động.
- **Nghiệm thu:** Đổi preset tạo build mới, không sửa package đã pin; build lại cùng input cho kết quả giống hệt.

### SCENARIO-01 — SOURCE/HTTP/DB TESTED · Draft ETag và immutable snapshot

- **Contract/current:** Draft GET/PUT xmin ETag, 428/400/412, locked draft/version numbering, immutable canonical snapshot/rubric/learner fields, receipt/audit và package-build job đã có. Không cần tạo GET draft mới.
- **Kiểm chứng:** HTTP/PostgreSQL actual migrations kiểm concurrent numbering, stale ETag, replay, rollback và geometry/runtime references. Anchor phải thuộc current Geometry attempt có Passed validation không Error/Critical; Failed QA hoặc stale attempt bị chặn ở validate/snapshot.
- **Còn chờ:** Package/toolchain Unity thật và matching binary/client deployment. Structural validation không thay geometry/package QA hoặc readiness.

### PLAYTEST-01 — P1 · SOURCE/HTTP/DB TESTED · Tenant, entitlement and launch

- **Contract/current:** OrganizationUser owner/session only; preparation pins accepted immutable package without entitlement/quota/grant; start checks current runtime + exact Building entitlement and consumes Trial atomically with receipt/audit. Separate five-minute grant and replay implemented. [Manual tests](playtest-manual-test.md).
- **Remaining acceptance:** Production configuration, real IFC/Unity/runtime client; no automatic Trial is provisioned. No playtest completion/sync/result API or learner analytics is claimed. Publish stays contained.

### RELEASE-01 — SOURCE/HTTP/DB TESTED · Publish gate (rollout pending)

- **Source:** DI uses ReleaseWriteStore with Publishing:Enabled=false by default. Publish checks live family/lifecycle/tenant, matching Active Training, current paid Building entitlement (Trial insufficient), exact approved content/rubric, readiness revision/version/validation/annotation and accepted current package/manifest/runtime provenance.
- **Docker evidence:** restricted executor, missing/revoked family, Trial/missing paid entitlement, stale attempt, lifecycle, concurrent publication, receipt/audit rollback and authorized replay are tested. Built does not certify real Unity output.
- **Pending:** Supabase gate/ACL migration, matching binary and feature-flag smoke, real provider/package/runtime acceptance. Learner launch and seat allocation remain separate.
### SESSION-01 — SOURCE/HTTP/DB TESTED · QR và training session

- **Trạng thái 12/10/2026:** đã có QR cấp Building, prepare/start/heartbeat/continuation (`/api/training/sessions`, `/api/qr/{token}`), seat tại start và sync sau expiry. Còn nghiệm thu Unity/Mobile. Nội dung dưới đây là lịch sử.

- **Contract/current:** QR Building, Training list, session preparation/start, launch grant và offline continuation/sync chưa có đủ API production.
- **Sửa code:** Sau ACCESS-01, APPROVAL-01, CAPACITY-01 và release/entitlement gates, triển khai QR cấp Building; list/package/prepare kiểm access, prepare pin release/scenario/package. Explicit online start recheck access/approved/published/entitlement/runtime, phân suất nguyên tử rồi cấp grant; pin entitlement/review/rubric.
- **Nghiệm thu:** Package cache không mở session mới offline; prepare/playtest không tính seat. Retry start không tạo session/seat trùng. Phiên đã start tiếp tục/sync sau expiry, rotate/revoke quyền hoặc account bị khóa; event/result vẫn kiểm owner và replay hash.

### ASSESSMENT-01 — SOURCE/HTTP/DB TESTED · Kết quả theo rubric (FR-TRAINING-01,08)

- **Trạng thái 12/10/2026:** kết quả theo rubric hash đã duyệt, outcome `Passed|NotPassed|Incomplete|NotAssessed` tách completion; rubric `schema_version` chuẩn `"1"` (#69). Nội dung dưới đây là lịch sử.

- **Contract/current:** Chưa có đầy đủ runtime/result API v7; session Completed không suy ra Passed.
- **Sửa code:** Lưu rubric hash đã duyệt, criterion results, score, outcome `Passed|NotPassed|Incomplete|NotAssessed` và lý do theo session/version. Trainee tự chọn mode, retry không giới hạn; AI bị chặn trong Assessment.
- **Nghiệm thu:** Kết quả cũ giữ rubric cũ sau publish mới; replay không ghi trùng; Completed không tự Passed; không prerequisite/module/sprint/certificate. Threshold/trọng số cần policy được chốt trước implementation.

## D. Billing, notification, AI, Learn và báo cáo

### BILLING-01 — P1 · CODE/TEST · PayOS và entitlement Building

- **✅ Có code:** Catalog/quotation Draft → Issued → Accepted; SDK payOS2.1.0, checkout idempotent, provider-confirmed cancel, verified durable inbox, ledger/provisioning atomic, cấp/gia hạn từng Building, lease/retry/reconcile, API trạng thái và entitlement. Trang return/cancel chỉ điều hướng. Contract/test tay tại [billing.md](billing.md).
- **✅ Kiểm chứng tự động:** SDK chữ ký offline, HTTP authorization/OpenAPI, PostgreSQL disposable cho replay/race/rollback/partial recovery, UTC month-end và grants executor. Kết quả cuối đợt ghi trong billing.md; không coi test mock là provider acceptance.
- **✅ Kiểm chứng local/provider một phần:** Sáu migration PayOS và login/grants đã áp vào Supabase cho test local; tạo link/QR provider thật và replay cùng operation đạt. Chưa chuyển tiền.
- **❌ Chưa nghiệm thu:** Runtime executor/worker deployment Azure, đăng ký webhook/probe PayOS, giao dịch thật với bank/provider và kiểm entitlement deployed. Không tick publish/playtest/training gate từ entitlement storage.
- **✅ Đã làm sau baseline:** Reminder 5 ngày (#68), revenue (#67), Upgrade, AI top-up/consume (#56), hủy báo giá/danh sách payment/enterprise workflow (#68).
- **❌ Chưa làm:** hoàn tiền tự động, eInvoice và FE billing đầy đủ. Không lập AI invoice cuối kỳ hoặc overage debt.
- **Nghiệm thu:** Return URL không ghi Paid; amount/currency/link sai không được apply; webhook/recovery/replay không cấp trùng; cùng payment có mốc kích hoạt chung, renewal nối kỳ đã mua; Paid và provisioning riêng biệt.
### NOTIFY-01 — SOURCE/HTTP/DB TESTED · Nhắc hết hạn

- **Trạng thái 12/10/2026:** worker nhắc trước 5 ngày (mặc định tắt), web notification `/api/organizations/me/notifications`, email có lease/at-least-once (#68). Mailgun thật chưa nghiệm thu. Nội dung dưới đây là lịch sử.

- **Contract/current:** Mailgun hiện phục vụ reset; reminder dịch vụ chưa có. Docs yêu cầu web/email trước 5 ngày.
- **Sửa code:** Thêm scheduler/outbox/delivery idempotent theo entitlement, kỳ và channel; gia hạn phải vô hiệu reminder cho kỳ cũ.
- **Nghiệm thu:** Retry/duplicate không gửi trùng một kỳ/kênh; email và in-app notification cùng đúng entitlement; kiểm provider thật riêng với unit test.

### AI-01 — SOURCE/HTTP/DB TESTED · AI request và quota

- **Trạng thái 12/10/2026:** request/quota pooled, reserve/settle/release, reconcile Admin, danh sách request của Organization (`GET /api/ai/requests`, #58). AI provider thật chưa có adapter (503 `AI_PROVIDER_UNAVAILABLE`). Nội dung dưới đây là lịch sử.

- **Contract/current:** V7 dùng Organization quota pooled trả trước từ Building/package/top-up và Trainee daily quota riêng; chưa có luồng production BE request/quota đầy đủ. Không còn overage consent, AI billing period hoặc nợ trả sau.
- **Sửa code:** Durable request/result qua BE; reserve quota trước billable work, settle/release idempotent và reconcile timeout. Giữ policy/usage/grant provenance, lock order request → ledger/reservation → grants theo ID. Quota request không tạo invoice; BILLING-02 cấp quota qua payment đã áp dụng.
- **Nghiệm thu:** Concurrent reserve không vượt quota; thiếu quota chặn request mới; duplicate/hash conflict, late result/timeout/release không trừ trùng; quota Trainee không consume pool Organization. Provider nằm ngoài transaction DB.

### CAPACITY-01 — SOURCE/HTTP/DB TESTED · Hạn mức người theo kỳ (FR-BILLING-11)

- **Trạng thái 12/10/2026:** seat theo distinct user/Building/kỳ tại start, Upgrade giữ kỳ và seat đã dùng (#56). Nội dung dưới đây là lịch sử.

- **Contract/current:** Chưa thấy learner-seat/upgrade v7 trong source.
- **Sửa code:** Một distinct Trainee user ID chiếm một seat tại Building/entitlement khi start lần đầu; nhiều bài/lượt cùng kỳ chỉ một seat. Tòa khác/kỳ renewal mới tính riêng. Upgrade giữ kỳ và số seat đã dùng; user đã tính được start tiếp khi access/service hợp lệ.
- **Nghiệm thu:** Hai user tranh suất cuối chỉ một thành công; retry cùng user không tăng count; login/list/prepare/playtest không tính. Upgrade không reset seats hoặc chồng kỳ cam kết.

### BILLING-02 — SOURCE/HTTP/DB TESTED · New/Renewal/Upgrade/top-up v7

- **Trạng thái 12/10/2026:** Upgrade, AI top-up, AI reserve/consume và learner seat allocation đã có (#56); hủy báo giá, danh sách payment, enterprise workflow (#68). Còn rollout và bằng chứng provider thật. Dòng "Pending" bên dưới là lịch sử.

- **Source:** package6/12 months, monthly price, learnerLimit and quota/policy configuration. Quote pins capacity/quota/policy and fixed UTC service interval before Issue. New/Renewal reservations prevent overlapping checkouts; Paid/Applied provisioning adds immutable entitlement and quota grant atomically from snapshot.
- **Docker evidence:** actual migrations, constrained grants, snapshot/catalog mutation, quota coverage, ETag/Issue concurrency, reservation/cancellation/late payment, provisioning rollback and retries. Legacy checkout recovery remains; no new sales from incomplete legacy snapshots.
- **Pending:** rollout of all API/worker instances and real provider evidence. Upgrade, top-up, AI reserve/consume and learner seat allocation were added in #56. See billing-v7-rollout.md and publish-billing-v7-manual-test.md.
### RAG-01 — NGOÀI PHẠM VI HIỆN TẠI · Learner-safe retrieval (FR-AI-03,05–06)

- **Trạng thái 12/10/2026:** không triển khai trong đợt này theo quyết định của nhóm; gồm `POST /api/ai/trainee/answer` và adapter AI provider thật. Nội dung dưới đây giữ làm yêu cầu khi mở lại.

- **Contract/current:** V7 có `scenario_knowledge_documents`; chưa thấy index/retrieval authorization production tương ứng.
- **Sửa code:** Chỉ index snapshot approved/published đúng hash gồm `name`, `objectives`, `instructions`; mỗi retrieval recheck current access/service trước vector retrieval. Loại draft/rubric/đáp án/private IFC. Learn pin post/version hợp lệ, Hidden có thể RAG, Deleted/Unpublished bị loại.
- **Nghiệm thu:** Access/service mất quyền chặn Building retrieval mới dù index/cache còn; vẫn đọc giải thích kết quả của chính mình đã lưu và Common hợp lệ. Assessment chặn AI; nguồn/version khác tenant bị từ chối.

### LEARN-01 — SOURCE/HTTP/DB TESTED · CMS và bookmark

- **Trạng thái 12/10/2026:** đã có post/version/situation/source/bookmark, public chỉ đọc Published, Hidden 410, media allowlist, danh mục nguồn/tình huống (#69) và Idempotency-Key cho lifecycle (#57). Nội dung dưới đây là lịch sử.

- **Contract/current:** Chưa có CMS/API/persistence Learn production. Docs quy định PlatformAdmin quản trị; không có bước approve riêng.
- **Sửa code:** Thêm post/version/situation/source/bookmark; public chỉ đọc Published; Hidden không public nhưng được phép RAG, Deleted bị loại; validate provider URL; audit/idempotency và cache invalidation qua outbox.
- **Nghiệm thu:** Draft không public; version Published bất biến; Hidden/Deleted không trả public, Deleted không vào RAG; bookmark chỉ thuộc Trainee; không thêm approve route ngoài contract.

### REPORT-01 — SOURCE/HTTP/DB TESTED · Analytics/support/audit views

- **Trạng thái 12/10/2026:** learner analytics (`/api/organizations/me/analytics/training`, `/api/admin/analytics/training`, theo tổ chức `/api/admin/organizations/{id}/analytics/training`), revenue, audit có tên/lý do (#67). Nội dung dưới đây là lịch sử.

- **Contract/current:** Có feedback/ticket/message cho user và admin, audit metadata admin-only, cùng operations analytics cho platform/organization. Operations analytics không phải learner analytics; chỉ số plays, active sessions, completion/duration chưa có nguồn session chuẩn.
- **Support đã sửa:** Canonical receipts, ETag/428/400/412, lifecycle/owner/admin locks, bounded pagination/filter và append-only messages; HTTP/PostgreSQL race/replay/rollback tests có bằng chứng. Supabase migration/ACL đã áp. **Còn chờ:** Support binary/client smoke test; audit filters/allowlist and readonly RepeatableRead operations snapshots now have source/Docker tests; deployment and learner analytics remain pending, learner metrics cần start/heartbeat/result chuẩn.
- **Nghiệm thu:** Playtest/preparation không tính learner play; dashboard/API dùng chung định nghĩa; người dùng không đọc tenant khác; dữ liệu nhạy cảm được che.

## E. Thứ tự phụ thuộc và cách hoàn tất task

1. **DB-01, AUTHZ-01, AUTH-01–04:** schema test, tenant boundary, đăng ký/onboarding/profile/session/recovery.
2. **IFC-01, IFC-02, IFC-03, IFC-04, SCENARIO-01, SCENARIO-02, LIBRARY-01, APPROVAL-01:** worker/QA, thiết bị phát hiện, draft/version, cấu hình tương tác, thư viện và approval; readiness và approval là hai dependency riêng. STYLE-01 làm sau khi worker Blender/Unity chạy được.
3. **BILLING-01–02, ACCESS-01, CAPACITY-01, NOTIFY-01, PLAYTEST-01, RELEASE-01:** payment/provisioning, access/capacity và publish gates.
4. **SESSION-01, ASSESSMENT-01:** start/launch grant, pin rubric, offline sync và kết quả theo mode.
5. **AI-01, LEARN-01, RAG-01, REPORT-01:** request/quota và retrieval đã cấp quyền; indexing cần approved/published version, analytics cần session/result chuẩn.

Task chỉ hoàn tất khi contract, handler/store/schema/gate và role/tenant đúng; có kiểm tra happy path cùng lỗi/race/replay phù hợp trên database test; tài liệu API phản ánh source mới; và PR ghi lệnh, kết quả, phần bị mock/bỏ qua, cùng giới hạn provider. Không coi build, route tồn tại hoặc mock test là bằng chứng provider/production đã hoạt động. Đợt selected API này đồng bộ cả BE docs và hồ sơ triển khai trong nhánh Docs riêng; không suy toàn bộ v7 đã triển khai.

Google onboarding/link: source/DI/controller + PostgreSQL migration/constraints/RLS, atomic create/replay/race/rollback đã có kiểm thử; Firebase thật, client và deployment chưa kiểm chứng. Migration chưa áp Supabase. Hướng dẫn: [google-auth-manual-test.md](google-auth-manual-test.md).

## Building mutation — code và test trong đợt API được chọn

POST body `organizationId`/alias query, admin PUT/DELETE derive tenant, validation nested theo field đã triển khai. Unit/HTTP và PostgreSQL chạy migration history thật, gồm runtime audit INSERT-only, rollback audit và race khóa lifecycle được ghi trong `building-manual-test.md`. Access/participation, IFC worker, readiness/approval, playtest và package build vẫn là các task riêng; không suy hoàn tất từ Building CRUD. Supabase schema/ACL đã kiểm trong rollout selected API; binary/client deployment chưa smoke test.

### Selected scope Task 2 — bound IFC upload (source/test/schema; provider pending)

Hai route initiate dùng chung intent/receipt và SHA-256 dự kiến. Complete kiểm owner/tenant/key/size/hash thực, lưu candidate trước copy, adopt atomic; cleanup có lease/retry và tombstone để xử lý late write. Migration additive `AddBoundIfcUploads` giữ source legacy unverified. Xem `ifc-upload-manual-test.md`. Test PostgreSQL isolated + HTTP/storage fake; schema Supabase đã áp và kiểm; không đánh dấu pipeline IFC/Blender/Unity, S3 hoặc binary production hoàn tất.

### Selected scope Task 3 — processing/outbox/HTTP worker (source/test; provider pending)

Process dùng verified source + Idempotency-Key và SQL gate atomic. Machine-only claim/renew/output/complete/fail, restricted executor, dispatcher receipt-before-ACK, lease fencing/recovery và retry gate đã triển khai. Artifacts/QA/issues chỉ lấy current accepted attempt; alias additive giữ các cột legacy. Xem `processing-worker.md`. Worker nghiệm thu bằng fake HTTP, chưa có IFC/Blender/Unity thật; dispatcher mặc định tắt. Readiness/approval/package runtime/playtest và publish chưa được suy hoàn tất từ kết quả này.


Scenario authoring: implemented durable create/snapshot/package-build receipts, draft ETag 428/400/412, locked numbering, immutable canonical v7 snapshot/rubric/learner fields and accepted geometry/runtime references. PostgreSQL actual-history plus fake package output tests ran; real Unity and deployment remain unchecked. See scenario-authoring.md. Structural validation alone is not readiness.

### Selected scope Task 5 — exact readiness and content approval

Source + PostgreSQL/HTTP tests implemented: exact revision/version/run/annotation/artifact provenance, Passed/blocker/runtime gate, separate immutable Submitted→Approved/Rejected content review, server hashes, live tenant/lifecycle, receipt and audit rollback. See [scenario-readiness.md](scenario-readiness.md). Tests use fake worker outputs, not Unity. Publish/learner sessions remain incomplete. Supabase Tasks 2–4 plus dependency EXECUTE repair were applied 2026-10-07; existing row counts were preserved. Older "deployment pending" notes describe their original implementation evidence, not the subsequent schema rollout.

Task 6: isolated actual-history PostgreSQL + HTTP/OpenAPI + fake runtime/package/paid-provider fixture verified; migrations are additive and preserve legacy sessions. Tasks7–9 now have source/test/docs: release/Training/access, support receipts/ETag/paging and OpenAPI inventory. Their migrations and final gate repairs have been applied/postchecked on Supabase; matching binary/client deployment remains pending.


### Schema rollout 2026-10-07

Supabase migrations through `20261007140000_RequirePassedGeometryAnchors` have been applied and postchecked; see [ifc-authoring-deployment.md](ifc-authoring-deployment.md). Existing 8 users/6 organizations/2 buildings were preserved. Earlier deployment-pending notes are historical evidence for their original task commits. Readiness/playtest source and isolated tests are verified; matching deployed API binary, production worker and runtime client remain unchecked. Tasks7–9 source/isolated tests are recorded in [selected-api-contract.md](selected-api-contract.md); release/access, support, privilege repair and final IFC/scenario function migrations are applied on Supabase. Publish gate source and disposable-database tests are implemented in the current branch; deployed feature enablement, real runtime and learner flows remain unverified.

## Selected scope Tasks7–9

[Current route checklist and evidence](selected-api-contract.md) replaces historical selected-API gap descriptions. [Generated route inventory](api-route-inventory.md) counts OpenAPI methods, not completed capabilities. Built/access/support are source/test/schema verified; deployed binary/client and real workers remain separate acceptance. Publish gate is source/test implemented with rollout disabled; learner start/sync/result remain incomplete.

Earlier selected review (historical evidence): IFC receipt Building binding259b6b5 and Passed geometry anchors5377654 committed/pushed. Selected HTTP/PostgreSQL regression36passed, supported IFC105passed, build0warning/error; Docker-only legacy IFC groups excluded. See task-progress-checklist.md and ifc-authoring-deployment.md for test/schema versus provider limits.
# Selected worker/API contract corrections

This section supersedes historical findings for the selected source paths: bounded pending-registration cleanup and Avatar recovery use restricted SQL gates; readiness/approval/catalog mutations recheck live family; preview verifies object existence/size; new approval/readiness audit exposes safe changes; annotations declare required If-Match and response ETag. See [manual checks and rollout](api-worker-contract-manual-test.md) and [worker permissions](worker-permissions.md). Source/HTTP/PostgreSQL/fake-provider evidence is recorded separately from deployment; do not infer Supabase/Azure rollout.

Publish has a paid-entitlement/approval/readiness/provenance gate and a rollout flag; it is no longer an unconditional503 implementation. Learner prepare/start/seat/events/results and training analytics remain incomplete. The unrelated GET BIM facts reader still assumes a `bim_facts` table not created by the current migration history; read-contract fixture tests do not certify that endpoint on a fresh full schema.

Current worker/API batch: full Auth/Billing/PayOS/Avatar/reporting/Redis511/511 and IFC118/118 passed;0 failed,0 skipped. Linux decoder/Avatar subset25/25 also passed. All six selected source tasks are implemented/tested; four new forward migrations and matching binary/provider rollout remain unverified on the shared environment. See manual checks for acceptance boundaries.
