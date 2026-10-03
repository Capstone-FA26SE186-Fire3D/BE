# Building billing / PayOS

Requirements: Docs FR-BILLING-01..10, workflows §11, technology §9/10. This implementation uses the current Building address source `building_locations.address`; it does not create a second address on `buildings`.

## Task 1: database foundation

`20261002090000_AddBuildingBilling` runs the embedded `Billing/Schema.sql`. It retains legacy quotation/package fields and rows; new quotes use their Building lines. `20261002090100_SyncBuildingBillingModel` records the EF model only; it does not repeat the DDL. Financial migrations do not offer a destructive down migration: recover with a forward migration.

Adds discount rules, quotation lines/snapshots/revision, per-Building entitlements, per-line provisioning records, reminder storage, enterprise requests, command receipts, checkout operations and verified-webhook inbox. These tables now back the checkout/inbox/recovery runtime described below. Legacy header package FK becomes nullable and is not authoritative for new quotes. Existing quotes are retained; incomplete legacy quotes are not silently converted into purchasable lines.

Dedicated NOLOGIN request/webhook executors receive only EXECUTE on their respective SECURITY DEFINER payment functions. They do not inherit the NOLOGIN ledger owner or have direct payment DML. PUBLIC, Supabase `anon` and `authenticated` are denied financial-table access. Actual deployment must use separated least-privilege backend identities, not a superuser/service owner, and verify role memberships. Never grant the webhook executor to the general API login. SQL records adapter attestations; the SDK adapter verifies signatures before invoking the webhook function.

The PayOS options section has `Enabled`, `ClientId`, `ApiKey`, `ChecksumKey`, `ReturnUrl`, `CancelUrl`. It defaults disabled; enabling requires credentials, HTTPS callbacks and dedicated executor connections. Real credentials stay in ignored local appsettings/User Secrets or deployment secret storage. The original foundation alone does not enable payment; deploy all runtime migrations and configure recovery before enabling checkout.

## Task 2: catalog và báo giá Building

`20261002100000_AddBillingCatalogRevisions` thêm revision catalog, response receipt và SQL hardening: FK discount, tenant/requester/purpose bất biến từ Draft, thu hồi PUBLIC quotation grants, PayOS gate chỉ nhận số nguyên VND và kiểm tổ chức hoạt động. Migration từ chối role PayOS có login/quyền quản trị hoặc executor kế thừa ledger owner/direct payment DML; cần sửa cấu hình quyền trước khi áp, không tự cấp backend membership. `20261002100100_SyncBillingCatalogModel` chỉ đồng bộ metadata EF. Không cần reset dữ liệu. Bốn migration nền billing đã được áp trong lượt trước; các migration runtime PayOS mới trong lượt này chưa áp Supabase.

| Method / route | Quyền | Header bắt buộc |
| --- | --- | --- |
| GET `/api/billing/service-packages` và `/{id}` | OrganizationUser: chỉ active; PlatformAdmin: cả inactive | Bearer |
| POST `/api/admin/service-packages` | PlatformAdmin | Bearer |
| PATCH `/api/admin/service-packages/{id}` | PlatformAdmin | Bearer, If-Match |
| GET `/api/admin/discount-rules` và `/{id}` | PlatformAdmin | Bearer |
| POST `/api/admin/discount-rules` | PlatformAdmin | Bearer |
| PATCH `/api/admin/discount-rules/{id}` | PlatformAdmin | Bearer, If-Match |
| POST `/api/billing/quotations` | OrganizationUser, Building của tenant hiện hành | Bearer, Idempotency-Key |
| GET `/api/billing/quotations` và `/{id}` | OrganizationUser: tenant mình; PlatformAdmin: xuyên tenant | Bearer |
| PATCH `/api/billing/quotations/{id}` | Chủ tenant hoặc PlatformAdmin, chỉ Draft | Bearer, If-Match |
| POST `/api/admin/quotations/{id}/issue` | PlatformAdmin | Bearer, If-Match |
| POST `/api/billing/quotations/{id}/accept` | OrganizationUser của tenant báo giá | Bearer, If-Match |
| POST `/api/billing/enterprise-quote-requests` | OrganizationUser | Bearer, Idempotency-Key |
| GET `/api/billing/enterprise-quote-requests` | OrganizationUser: tenant mình; PlatformAdmin: toàn nền tảng | Bearer |
| GET `/api/admin/enterprise-quote-requests` | PlatformAdmin | Bearer |

Catalog PATCH nhận **đầy đủ** các field editable, không phải JSON Merge Patch. Package: `code`, `name`, `unitPrice`, `durationMonths`, `isActive` (mặc định true), `description` (tùy chọn). Server gán VND; `unitPrice` là giá **mỗi Building mỗi tháng**. Code package uppercase, 1–50 chữ/số/underscore/hyphen; name tối đa 255. Giá không âm, số nguyên VND, tối đa 999.999.999.999; duration phải dương. Giá một dòng = unitPrice × durationMonths. Package 0 đồng có thể lưu nhưng báo giá tổng 0 bị từ chối khi issue (`409 ZERO_AMOUNT_NOT_SUPPORTED`); chưa có flow cấp quyền miễn phí.

Discount: `code`, `discountKind` (`Percent` hoặc `Fixed`), `discountValue`, `minimumBuildings`, `validFrom`; tùy chọn `validUntil`, `servicePackageId`, `minimumDurationMonths`, `isActive`. Percent 0–100, tối đa hai số lẻ; Fixed là VND nguyên. Ngày phải là ISO 8601 có `Z` hoặc offset. Xét số **dòng đủ điều kiện** theo package/duration và hiệu lực. Chọn một rule giảm tiền lớn nhất, bằng nhau chọn rule ID ổn định. Không cộng dồn; clamp theo subtotal hợp lệ, percent làm tròn VND AwayFromZero, phân bổ theo tỷ lệ lấy floor, số dư 1 VND theo line ID. Không áp rule cho dòng ngoài scope. Snapshot giữ rule/value/amount, danh sách dòng eligible và quy tắc rounding.

Draft có 1–100 Building khác nhau, cùng tổ chức, active, có tên và địa chỉ. `New` dành cho Building chưa có lịch sử dịch vụ đã trả tiền; `Renewal` cần lịch sử entitlement paid (trial không tính). Body không nhận giá, tenant hoặc người tạo từ client:

```json
{
  "items": [
    { "buildingId": "<Building UUID>", "servicePackageId": "<Package UUID>", "purchaseAction": "New" }
  ]
}
```

POST trả `201`, Location, body và ETag. Cùng actor/operation/Idempotency-Key/input chuẩn hóa trả lại **response tạo ban đầu**, kể cả resource đã đổi trạng thái sau đó; dùng GET để lấy trạng thái hiện tại. Input khác trả `409 IDEMPOTENCY_KEY_CONFLICT`. Key 1–128 ASCII printable, không space. Receipt/audit/resource cùng transaction; quyền account/tenant kiểm lại từ database dưới khóa lifecycle.

GET detail trả ETag dạng `"billing-<id dạng N>-<revision>"`. PATCH/issue/accept: thiếu If-Match `428`, sai định dạng `400`, cũ `412`; chỉ một request cùng revision được commit. Issued snapshots không bị sửa khi catalog hoặc Building thay đổi. List quotations/enterprise phân trang mặc định 20, tối đa 100, thứ tự createdAt giảm rồi id; header/line và count/page đọc cùng snapshot PostgreSQL.

Admin issue đọc lại giá/catalog hiện hành và chốt snapshot tên/địa chỉ Building, package/duration/price/discount/terms. Body bắt buộc `taxAmount` nguyên VND không âm, `terms` plain text 1–10.000 ký tự và `validUntil` tương lai có timezone. Tax được admin cung cấp rõ ràng, chưa tự suy thuế suất. Draft hết hạn hiển thị sau 7 ngày; admin issue quyết định expiry cuối. Issue không charge. Accept chỉ `Issued` còn hạn, ghi acceptedAt một lần, không cấp entitlement hoặc tạo payment. Legacy quotation thiếu Building lines được giữ nhưng không thể issue; tạo báo giá mới thay vì suy scope từ header legacy.

Enterprise request có `requestedBuildingCount` dương, `requestedDurationMonths` tùy chọn dương, `contactName` (1–255), `contactEmail` hợp lệ; `contactPhone` (1–50) và `notes` (1–10.000) tùy chọn. Trả `201`, lưu trạng thái New, audit và receipt. Chưa có workflow admin chuyển enterprise request thành quotation. Không tạo charge/entitlement từ thông tin liên hệ.

Lỗi nghiệp vụ dùng ProblemDetails với `code`, `errors` theo field nếu có và `traceId`; lỗi JSON/model binding theo format validation ASP.NET hiện hành. Resource ngoài tenant trả 404. Checkout, verified webhook, provisioning và reconcile hiện đã có runtime/test như mô tả bên dưới. Reminder, revenue và AI settlement chưa triển khai; publish/playtest gate không được đánh dấu hoàn tất từ bảng entitlement mới.

## Test tay bằng Swagger

1. Admin tạo package: `{"code":"MONTH","name":"Building 1 tháng","unitPrice":100000,"durationMonths":1,"isActive":true}`.
2. OrganizationUser dùng Building đã tạo của mình và package ID vừa nhận để POST quotation; nhập một Idempotency-Key. Lưu quotation ID và ETag.
3. Admin issue với ETag: `{"taxAmount":0,"terms":"Dịch vụ Building trong một tháng","validUntil":"<timestamp tương lai có Z>"}`. Lưu ETag mới.
4. OrganizationUser accept với ETag mới, rồi GET để kiểm tra Accepted và acceptedAt. Tiếp tục các bước thanh toán ở phần nghiệm thu PayOS phía dưới.
5. Thử create lại cùng key/body: cùng quotation và response ban đầu. Đổi Building/package nhưng giữ key phải trả 409. Thử accept bằng ETag cũ phải trả 412. Tài khoản tổ chức khác không đọc được quotation này.

## Verification

### PayOS runtime foundation (Task 1)

SDK `payOS` pinned to 2.1.0 behind `IPayosProvider`. Create/Get/Cancel responses use the SDK's signature verification, timeout15s and no automatic retries. Webhook verification is offline; unknown/duplicate JSON fields are rejected to avoid silently dropping signed data.

Additive migrations AddPayosRuntime + SyncPayosRuntimeModel persist provider input before calls, request/payment-link binding, retry/lease metadata and a bounded unique order-code sequence. Runtime connections `ConnectionStrings:PayosRequestExecutor` and `ConnectionStrings:PayosWebhookExecutor` are mandatory when checkout/recovery is enabled. Credentials belong in deployment secrets, never source. `PayOS:WorkerEnabled` is independent from `Enabled`; both default false until complete runtime is deployed. Callback URLs must be non-loopback HTTPS.

Task1 verified six offline/config/PostgreSQL tests using disposable loopback database. Supabase runtime migration and live provider calls are not performed by those tests. Checkout/webhook endpoints are delivered in subsequent tasks.

Billing tests opt in through `FET3D_BILLING_TEST_ADMIN`, accept only a loopback PostgreSQL host and the `postgres` admin database, and create/drop a dedicated `fet3d_billing_test_*` database for each test. They never read application settings or User Secrets. Tests execute the real additive migration on a model-created relational baseline; this does not certify the full historical migration chain or production grants.

Example: `FET3D_BILLING_TEST_ADMIN=Host=127.0.0.1;Port=<disposable-port>;Database=postgres;Username=<test-admin>` followed by `dotnet test Fire3D/Fire3D.AuthTests --filter FullyQualifiedName~Billing`.

HTTP fixture thay authentication bằng danh tính test và vô hiệu toàn bộ hosted workers; vẫn kiểm role/tenant/account từ DB. Test này không chứng minh JWT production hoặc S3/FCM/Mailgun/PayOS thật đã hoạt động. Có test cạnh tranh create/accept, giá nhiều dòng/làm tròn, bất biến snapshot, idempotency conflict, quyền tenant, rollback audit và metadata OpenAPI/header. Chưa chứng minh deployment hoặc tiền thật.

### Checkout runtime (Task 2)
POST /api/payments/payos/create requires an active OrganizationUser session, Accepted unexpired VND quotation and Idempotency-Key. First Ready=201, replay=200, uncertain=202 with checkout Location. GET /api/payments/payos/checkouts/{id} is tenant scoped (Admin can inspect). POST /api/payments/payos/requests/{id}/cancel requires key and provider-confirmed cancellation; a signed paid webhook wins cancellation. Provider input/orderCode are committed before network calls, result before the executor bind. Lease is renewed between bounded calls; retries query the original orderCode and create only after confirmed absence. Healthy polling resets failure count; 10 consecutive failures require reconcile. Three HTTP/PostgreSQL tests passed with fake PayOS; no live payment or Supabase change.

### Verified webhook (Task 3)

Cancel replay contract: the original actor/key/input observes current checkout state under lock, with no provider call, audit or mutation. Terminal (Cancelled/Expired/Completed/Failed) returns200, nonterminal returns202. A late verified payment can make a previous cancellation Completed; clients must inspect checkoutStatus, not interpret200 as cancellation. A new cancellation after Paid is409 PAYMENT_ALREADY_PAID; changed input for the same key remains409 IDEMPOTENCY_KEY_CONFLICT.

POST /api/payments/payos/webhook is JWT-anonymous and SDK-signature protected. It persists only normalized signed payment identity, amount and reference, omitting bank/customer payload; response 200 follows inbox commit. Replays match all normalized input; reference/hash conflicts return409. Valid unknown/probe orders are ACKed and quarantined for investigation, never granted service.

The EXECUTE-only process_payos_inbox gate atomically records Received→Verified→Applied or Rejected, marks Paid, closes checkout, inserts per-line provisioning and audit. Legacy apply primitive is revoked from runtime executors. Mismatched VND/amount/payment-link ID and a second reference on an already Paid quotation are recorded Rejected; accepted money for a previously Cancelled/Expired request can still become Paid through this gate. An unbound webhook stays NeedsReconcile until checkout binding recovers. Ledger/provisioning rollback together on audit failure.

Task3 verification: 12 PayOS tests passed (SDK/config, HTTP and disposable PostgreSQL); covers invalid signature, amount/currency/link mismatch, input conflict, early/late delivery, duplicate, parallel lease claims and audit rollback. Provider delivery/probe and production permissions remain deployment acceptance steps. See official webhook ACK contract: https://payos.vn/docs/du-lieu-tra-ve/webhook/.

### Provisioning and recovery (Task 4)

GET /api/payments/payos/requests/{id} reports payment status, Applied transaction ID/status and each quotation line independently. GET /api/billing/entitlements is tenant scoped, paginated20/max100; Admin may filter organization/Building. IsEffective requires Active, active organization/Building, nondeleted lifecycle and UTC startsAt <= now < endsAt; Trial never authorizes paid training.

Only Applied BuildingService payments provision snapshot quotation lines. A short executor transaction locks payment, all quotation Buildings ascending, then the line. Calendar renewal uses DateTime.AddMonths in UTC; new lines share immutable request.paidAt, renewal starts at max(paidAt,last purchased noncancelled period end). A fresh entitlement preserves provenance; record/audit/entitlement commit together. Archived/disabled scope or a second New purchase for an already entitled Building remains Paid with NeedsReconcile.

PayosRecoveryWorker is registered in runtime, enabled only by PayOS:WorkerEnabled (defaultfalse). It polls30s, claims60s fenced leases, retries30s to30min, caps consecutive failures at10. Checkout calls renew leases between bounded provider calls; provisioning renews under its short DB lock. POST /api/admin/payments/payos/checkouts/{id}/reconcile validates admin/session and only reschedules existing uncertain checkout/inbox/failed lines. Succeeded lines and Applied money never change. Disabling PayOS:Enabled blocks only new checkout; keep worker/credentials enabled for existing payments.

Task4 verification: all19 PayOS tests passed with disposable PostgreSQL and fake provider. Covers partial recovery/shared activation, early renewal, UTC month-end/leap year, competing renewal payments, stale/null leases and rollback on entitlement audit failure. No real provider or Supabase runtime changes yet. Migration AddPayosProvisioningGates forward-refreshes earlier runtime gates, including final tenant/Building/expiry checks and null-lease fencing.

### Final contract and verification (Task 5, 2026-10-02)

Public FET3D return/cancel pages are hosted at `/billing/payment-return/` and `/billing/payment-cancel/`. They never write payment state. OpenAPI uses relative server `/`, metadata-based Bearer security, anonymous verified webhook and required Idempotency-Key on create/cancel. Deploy/configuration, least-privilege checks and the manual payment procedure are in [payos-deployment.md](payos-deployment.md).

Whole-branch review reproduced two recovery bugs, now fixed: a fully paid provider link after a lost create response can bind the reservation before verified inbox application, and admin reconcile can reschedule Pending inbox/provisioning jobs whose tenth claim was interrupted and lease expired. GET alone does not record money. SDK transport tests prove HTTP401/429/500 create is attempted once, HTTP404 absence is distinguished from unknown HTTP200 business errors, and official signatures reject tampering. Provider-specific missing-order business codes remain a live contract acceptance item.

Final validation on this worktree:

| Check | Result / limit |
|---|---|
| `dotnet build Fire3D/Fire3D.slnx --no-restore` | Pass, 0 warning / 0 error |
| Auth full suite, all configured test databases disposable loopback | After review fixes: 213 passed / 0 failed / 0 skipped, including Billing/PayOS; run independently of IFC |
| IFC filtered regression | 104 passed / 0 failed / 0 skipped; Docker classes below excluded |
| EF `has-pending-model-changes --no-build` (Task 5 evidence before follow-up fixes) | None; follow-up fixes add no migration or model changes |
| EF runtime idempotent script generation (Task 5 evidence) | Pass; six additive migration records since SyncBillingCatalogModel, not applied to Supabase |
| Non-superuser runtime migration (Task 5 evidence) | Pass with dedicated schema-owner migration identity; preserves existing user/quotation data and restores temporary schema CREATE grants |

The earlier Task 5 run had 202 passed / 3 failed / 0 skipped (205 total). Those failures were independently reproduced on detached baseline `f68c20e`: missing refresh rate limiting, Int64 assertion against Int32 generation, and unsupported PostgreSQL `min(uuid)`. The follow-up fixes resolve all three and add cancellation replay coverage. The final Auth/Billing/PayOS run passes all 213 tests; the user's root `AuthIntegrationTests.cs` modification remains untouched.

Follow-up verification: cleanup ordering/retention/lock recheck (4 PostgreSQL tests), resend cooldown/concurrent generation/stale job and OTP proof behavior (7 tests), refresh HTTP/security/anonymous regression (23 tests), cancellation/checkout/webhook regression (9 tests). Each targeted run passed before the full suite. Native disposable PostgreSQL 17 replaced unavailable Docker; no test connection was read from User Secrets. A first full Auth run overlapping IFC passed212/failed1 during test database disposal (`DROP DATABASE ... WITH (FORCE)` read timeout); rerunning Auth alone passed213/failed0/skipped0 without weakening assertions or changing fixtures.

Final follow-up review also reproduced a refresh CORS integration failure: the limiter returned429 before CORS, and `Retry-After` was not exposed. A separate forward fix runs CORS first and exposes the retry header. The production-pipeline regression now checks allowed Origin, credentials, exposed retry header, ProblemDetails and preflight204 after quota exhaustion. Four task commits are preserved; this review correction has its own commit.

IFC `IfcWriteSqlTests`, `ScenarioStoreTests` and `PlaytestEntitlementContainmentTests` require Docker unavailable in this run. An earlier broader command had two Docker setup failures, not a source assertion failure. Other IFC tests used loopback disposable PostgreSQL where required. Billing fixtures execute real runtime SQL on a model-created baseline; Auth PostgreSQL fixtures also apply the historical EF migration chain to disposable databases. Neither proves production grants/provider behavior.

No real provider call, shared database write, email/push delivery or production payment was performed. Runtime migrations, production executor credentials/grants, webhook registration/probe, return-page deployment and the manual 2,000VND payment remain deployment acceptance. Unbound money rejected by scope/expiry gates remains durable and requires investigation; reminder/revenue/AI settlement and publish/playtest/training gates are still outside this delivery.
