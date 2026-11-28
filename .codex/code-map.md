# Bản đồ code BE — đọc trước khi sửa

Cập nhật 28/11/2026, theo `main` sau PR #246. File này mô tả **code đang có**. [context.md](context.md) là nhật ký thiết kế theo thời gian, nhiều đoạn đầu đã cũ (ví dụ "ba vai trò", "PlatformAdmin approval"). Khi hai file lệch nhau, tin file này và code.

## 1. Đọc theo thứ tự

1. File này.
2. [sql-live-functions.md](sql-live-functions.md): mỗi hàm SQL đang chạy nằm ở file nào.
3. Tài liệu nghiệp vụ trong `docs/` của phần sắp sửa (bảng ở mục 4).
4. [workflow.md](workflow.md) và [lessons.md](lessons.md) cho quy trình chung.

## 2. Hệ thống trong một phút

FET3D huấn luyện PCCC trên mô hình 3D thật của công trình. Repo này là API trung tâm; các repo khác cùng tổ chức GitHub: `FE` (web), `Mobile` (Android), `AI` (RAG, schema `ai_service`), Unity (học viên), `Docs` (yêu cầu, pháp lý).

**Bốn vai trò** (`Fire3D.Domain/Enums`, `user_role_enum`):

| Vai trò | Thuộc tổ chức | Làm gì |
|---|---|---|
| `PlatformAdmin` | Không | Quản trị nền tảng, catalog, báo giá, tài khoản; **không thẩm định** (#245) |
| `OrganizationUser` | Có | Công trình, IFC, hạ tầng PCCC, tình huống, xuất bản, thanh toán |
| `Trainee` | Không | Học, luyện, thi trong Unity/Mobile |
| `FireSafetyExpert` | Không | Thẩm định hạ tầng và tình huống; chỉ do quản trị tạo |

Dùng chữ "thẩm định", không viết "phê duyệt phương án" (Điều 15 Nghị định 105/2025/NĐ-CP).

**Luồng chính Flow 1 (PC06):**
1. Tải IFC.
2. Worker dựng hình và QA.
3. Xác nhận thiết bị (`equipment-decisions`).
4. Hạ tầng PCCC phiên bản 1 → gửi thẩm định → Chuyên viên được gán quyết.
5. Tình huống trên hạ tầng đã Approved → gửi thẩm định.
6. Dựng gói Unity → xuất bản release.
7. Học viên chơi; server chấm điểm từ sự kiện.

## 3. Kiến trúc và đường đi của một request

```
Fire3D/
  Fire3D.API/             controllers, Program.cs, Authorization/, Extensions/, OpenApi/
  Fire3D.Application/     contract (record), validator thuần, interface store, handler MediatR
  Fire3D.Infrastructure/  store EF/Npgsql, worker nền, Persistence/Sql/*.sql, Billing/*.sql, Migrations/
  Fire3D.Domain/          entity, enum
  Fire3D.AuthTests/       test tích hợp Postgres (đa số), test HTTP qua WebApplicationFactory
  Fire3D.IfcTests/        test IFC, validator thuần, SQL đọc
contracts/editor/v1/      schema + fixture editor fet3d.editor/1 (dùng chung với web và Unity)
docs/                     tài liệu từng phần, api-route-inventory.md (đối chiếu với OpenAPI bằng test)
```

**Đường đi điển hình:**
1. Controller kiểm hình dạng input, header (`If-Match`, `Idempotency-Key`).
2. Gọi service hoặc store ở Infrastructure.
3. Store gọi **hàm cổng SQL** (`*_gate`) qua [JsonCommandGate](../Fire3D/Fire3D.Infrastructure/Persistence/JsonCommandGate.cs) hoặc Npgsql thẳng.
4. Gate là `SECURITY DEFINER`, trả `{code, status, result, errors?}`. Gate tự làm: kiểm quyền, kiểm tenant, khoá (advisory lock + `FOR UPDATE`), ETag, idempotency receipt, ghi `audit_logs`, thông báo. Tất cả trong **một giao dịch**.
5. Controller đổi `code`/`status` thành ProblemDetails `{title, code, errors}`.

**Quyền DB:**
- Login runtime (`fire3d_backend`, `fet3d_payos_local_api`, …) là thành viên `fire3d_api`, chỉ có EXECUTE trên gate.
- Chủ sở hữu hàm:
  - `fet3d_ifc_upload_owner`: phần lớn gate;
  - `fet3d_payos_ledger_owner`: PayOS và cấp dịch vụ;
  - `fet3d_pending_cleanup_owner`: dọn đăng ký treo.
- Bảng mới: bật RLS, policy cho role chủ gate, thu hồi `anon`/`authenticated` (Supabase PostgREST). Nếu bảng tham chiếu `users`, cấp SELECT kèm policy cho `fet3d_pending_cleanup_owner` (bài học #235).
- `JsonCommandGate` chỉ gọi gate có trong danh sách trắng; gate mới phải thêm vào đó.

**Phân quyền ở API:**
- Mọi controller mặc định cần đăng nhập (`AuthorizationPolicies.cs`). Policy `PlatformAdministration` chỉ gồm `PlatformAdmin`.
- [ExpertRouteGuard](../Fire3D/Fire3D.API/Authorization/ExpertRouteGuard.cs) chặn mặc định: token Chuyên viên nhận 403 ở mọi `/api` ngoài `/api/expert/*`, `/api/auth/*`, `/api/me/*`. **Route mới không mở cho Chuyên viên trừ khi thêm vào guard.**
- [PasswordChangeGuard](../Fire3D/Fire3D.API/Authorization/PasswordChangeGuard.cs): phiên ở bước `ChangePassword` chỉ đổi mật khẩu, `me`, refresh, logout.
- Tenant: gate lấy tổ chức từ actor. Tài nguyên của tổ chức khác trả **404**, không 403.

**Quy ước HTTP:**

| Tình huống | Trả |
|---|---|
| Thiếu `If-Match` | 428 `PRECONDITION_REQUIRED` |
| ETag cũ | 412 `PRECONDITION_FAILED` |
| Thiếu `Idempotency-Key` | 400 `IDEMPOTENCY_KEY_REQUIRED` |
| Cùng key, khác nội dung | 409 `IDEMPOTENCY_KEY_CONFLICT` |
| Cùng key, cùng nội dung | trả lại kết quả lần đầu |
| Route đã gỡ | 410 (ví dụ `ADMIN_REVIEW_REMOVED`) |

## 4. Tra theo nghiệp vụ

| Nghiệp vụ | Controller | Code chính | Gate / SQL | Tài liệu |
|---|---|---|---|---|
| Đăng nhập, đăng ký OTP, Google, đổi/quên mật khẩu, đăng nhập lần đầu Chuyên viên | `Auth`, `Accounts`, `Google*`, `Avatar` | `Application/Authentication`, `Infrastructure/Authentication` (`AuthStore`, `FirstLoginVerificationStore`) | EF + `cleanup_pending_registrations`, trigger `users_email_only_while_unverified` | `authentication.md`, `password-reset.md`, `administration.md` |
| Công trình, hồ sơ PC06, mã tham gia/QR | `Buildings`, `BuildingAccess`, `BuildingPlanDocuments`, `OrganizationProfile` | `Application/Buildings`, `Infrastructure/Buildings` | `building_qr_gate` | `building-pc06.md`, `release-building-access.md` |
| IFC upload, xử lý, QA, preview | `IfcCommands`, `IfcQueries`, `Revisions`, `EditorPreview`, `GeometryWorker`, `ProcessingWorker` | `Infrastructure/Ifc` (`IfcReadStore*`, `ProcessingDispatcher`, `Redis*`) | `ifc_upload_gate`, `processing_*_gate` | `processing-worker.md`, `geometry-worker.md`, `ifc-qa-codes.md`, `redis-processing.md` |
| Xác nhận thiết bị | `EquipmentCandidates` | `Infrastructure/Ifc/EquipmentCandidates*` | `revision_equipment_gate` | `ifc-equipment-candidates.md` |
| Hạ tầng PCCC | `FireInfrastructures` | `Application/FireSafety/FireInfrastructures.cs` (luật item, `SeedItems`) | `fire_infrastructure_gate` | `fire-infrastructure.md` |
| Tình huống: nháp, snapshot, readiness, gửi thẩm định | `Scenarios`, `ScenarioRubrics`, `ScenarioInteractions`, `ScenarioContentReviews`, `PlanDossier` | `Application/Editor/EditorContract.cs`, `Application/Scenarios`, `Infrastructure/Scenarios` | `scenario_authoring_gate`, `scenario_readiness_gate`, `plan_dossier_gate` | `scenario-authoring.md`, `scenario-readiness.md`, `plan-dossier.md` |
| Thẩm định, phân công xoay vòng | `ExpertReviews`, `AdminExpertReviews` | `Infrastructure/Expert` | `expert_review_gate`, `expert_review_admin_gate`, `expert_assign_pending`, trigger trên `scenario_content_reviews`, `fire_infrastructures`, `users` | `expert-review.md` |
| Gói Unity, release, playtest | `ScenarioPackageBuilds`, `PackageWorker`, `Releases`, `Playtests` | `Infrastructure/Releases`, `Infrastructure/Scenarios/Playtest*` | `publish_release_gate`, `release_access_gate`, `playtest_*_gate`, `processing_package_lease_gate` | `unity-package-worker.md`, `playtest-manual-test.md` |
| Phiên học, sự kiện, chấm điểm | `TrainingSessions` | `Infrastructure/Learning/LearnerSessions*` | `learner_session_gate` (events:batch, rubric server-side) | `learner-sessions.md` |
| Báo cáo, analytics | `LearnerAnalytics`, `OperationsAnalytics`, `OrganizationWorkInProgress` | `Infrastructure/Reporting` | `analytics_*_gate` | `learner-analytics.md`, `reporting-operations.md` |
| AI (hỏi đáp, bản nháp, nguồn tri thức) | `OrganizationAi`, `KnowledgeSourceFiles` | `Application/Ai/OrganizationAiContracts.cs`, `Infrastructure/Ai` | `ai_org_gate`, `ai_worker_gate`, `ai_admin_gate`, `ai_trainee_gate` | `organization-ai.md` |
| Báo giá, thanh toán PayOS, cấp dịch vụ, nhắc hạn | `Billing`, `Payos`, `OrganizationNotifications` | `Infrastructure/Billing` (`BillingService.*`, `PayosPayments.*`, `BillingReminders`) | `Billing/*.sql`: `process_payos_inbox`, `claim_payos_provisioning_context`, `finalize_payos_provisioning` | `billing.md`, `billing-v7-rollout.md`, `payos-deployment.md` |
| Learn, thư viện tổ chức, catalog runtime, gói dùng thử | `Content`, `LearnMedia`, `PlatformServiceAdmin` | `Infrastructure/Content` | `learn_*_gate`, `library_gate`, `content_lifecycle_gate`, `runtime_catalog_gate`, `trial_entitlement_gate` | `learn-library.md` |
| Hỗ trợ, audit | `Support`, `AuditLogs` | `Infrastructure/Support`, `Infrastructure/Administration` | `support_command_gate` | `support-api.md` |

## 5. Hàm SQL nào đang chạy

Một hàm có thể bị `CREATE OR REPLACE` ở nhiều file SQL. Bản đang chạy là bản trong **migration có timestamp lớn nhất** nhúng file đó.

- **Cách nhúng:** migration nạp file qua `GetManifestResourceStream("<LogicalName>")`. `LogicalName` khai trong `Fire3D.Infrastructure.csproj`.
- **Tra nhanh:** [sql-live-functions.md](sql-live-functions.md), sinh bằng script, kèm cách chạy lại.
- **Khi sửa một gate:** chép thân từ bản live rồi chỉ đổi đúng chỗ cần. Tốt nhất lấy từ prod bằng `pg_get_functiondef` (chỉ đọc) và so md5 với bản trong repo. Đừng chép từ một file SQL cũ hơn.

## 6. Worker nền (18 `BackgroundService`)

Đăng ký trong `Fire3D.API` (`AddHostedService`). Phần lớn có cờ bật/tắt trong cấu hình (`*:Enabled`, `AuthEmail:WorkerEnabled`, …).

| Worker | Việc |
|---|---|
| `EmailVerificationWorker`, `RegistrationOtpEmailWorker`, `PasswordResetWorker`, `FirstLoginEmailWorker` | Gửi email từ hàng đợi Postgres có lease (Mailgun) |
| `PendingRegistrationCleanupWorker`, `RefreshTokenCleanupWorker`, `AvatarCleanupWorker` | Dọn dẹp định kỳ |
| `ProcessingDispatcher`, `RedisProcessingPublisher`/`Consumer`/`Recovery` | Giao việc IFC cho worker (HTTP hoặc Redis stream), phục hồi |
| `IfcUploadCleanupWorker` | Dọn object S3 rác |
| `OrganizationAiWorker`, `KnowledgeSourceWarmWorker` (`AiSourceWarming.cs`) | Gửi yêu cầu AI, làm nóng nguồn |
| `PlatformCacheInvalidationWorker` | Đồng bộ cache catalog |
| `PayosRecoveryWorker` | Phục hồi checkout, inbox webhook, cấp dịch vụ |
| `BillingReminderWorker` (`BillingReminders.cs`) | Nhắc hạn dịch vụ (web, email, FCM) |
| `StaleLearnerSessionWorker` | Đóng phiên học treo |

Cơ chế chung: claim bằng lease (`lease_token`, `lease_until`), gia hạn khi làm lâu, hết hạn thì worker khác nhận lại. Lần nhận sau ghi `Expired` cho lần trước (xem `endReason`, #239).

## 7. Viết migration

1. Viết file SQL ở `Persistence/Sql/<Tên>.sql` (billing thì `Billing/`).
2. Nhúng file vào `.csproj` với `LogicalName`.
3. Tạo class `Migrations/<timestamp>_<Tên>.cs` có `[DbContext]`, `[Migration("…")]`. `Up` đọc resource; `Down` ném `NotSupportedException`.
4. Đổi entity hay model EF thì cập nhật `Fire3DDbContextModelSnapshot.cs`. Nếu không, `MigrateAsync` báo "pending model changes".
5. **Owner-role dance** khi thay hàm của role chủ: tạm cấp role chủ cho `current_user` (`WITH SET TRUE, INHERIT TRUE`), `GRANT CREATE ON SCHEMA`, làm việc, rồi trả lại đúng như cũ. Mẫu: `ExpertAssignmentFixes.sql`, `PayosLatePaymentShift.sql`.
6. Cuối file: `ALTER FUNCTION … OWNER TO <role chủ>`, `REVOKE ALL … FROM PUBLIC, anon, authenticated`, `GRANT EXECUTE` gate cho `fire3d_api`, `fet3d_backend_executor`.
7. Migration billing: thêm vào chuỗi migration cố định trong `Fire3D.AuthTests/BillingDatabase.cs`, vì test billing không chạy toàn bộ lịch sử.
8. Route mới: thêm vào `docs/api-route-inventory.md` (có test đối chiếu OpenAPI). Header dùng `[RequiredRequestHeader]` hoặc `[OptionalRequestHeader]`.

## 8. Test

- **Chạy:**
  - Postgres và Redis chạy trong Docker; biến môi trường lấy từ `.codex/local/testenv.sh` (local, không commit).
  - Mỗi test Postgres tự tạo một database riêng.
  - Lệnh: `dotnet test Fire3D.AuthTests --no-build --filter "FullyQualifiedName~<TênPhươngThức>"`.
- **Chạy theo tên phương thức, không theo tên file:** test nằm trong `partial class AuthIntegrationTests`, nên lọc theo tên file không khớp test nào.
- **Bẫy môi trường đã gặp:**
  - Chạy cả bộ lớn một lượt có lần treo hàng giờ: nên chạy từng test, có `timeout`.
  - Docker Desktop tự tắt giữa chừng: lỗi `No password has been provided` nghĩa là mất Postgres, không phải lỗi code.
  - Worktree mới phải có thư mục `.codex/local/`: một test OpenAPI ghi `openapi.json` vào đó.
  - Đừng build hoặc test song song khi đang chạy bộ lớn: máy quá tải sẽ sinh timeout giả.
- **Helper hay dùng** (đều nằm trong `partial class AuthIntegrationTests`):
  - `SeedReadyPackage`, `SubmittedContentReview`: dựng gói sẵn sàng và mục chờ thẩm định.
  - `RoleClient(role, org?)`: client HTTP đã đăng nhập theo vai trò.
  - `SharedExpert()`: một Chuyên viên dùng chung trong cả test.
  - `DecideAsExpert(version, approve)`: dựng dữ liệu "đã duyệt" hoặc "chưa đạt" bằng route thật. Quản trị không duyệt được nữa.

## 9. Lên production

**Merge PR không tự áp migration.** Pipeline `.github/workflows/azure-deploy.yml` chạy preflight chỉ đọc; thiếu migration thì chặn deploy.

Quy trình áp, chỉ làm khi người phụ trách đồng ý:
1. Sinh script idempotent: `dotnet ef migrations script <A> <B> --idempotent --no-build -p Fire3D.Infrastructure -s Fire3D.API`.
2. Thêm đoạn chặn chạy nhầm database ở đầu script.
3. Backup bằng `pg_dump -Fc -n public`.
4. Áp script.
5. Kiểm tra lại lịch sử migration, chủ sở hữu và quyền của hàm.

Prod là Supabase, kết nối qua pooler. Không ghi connection string hay mật khẩu vào file chung.

## 10. Git

- Nhánh mỗi issue: `feature/…`, `fix/…`, `docs/…`, tạo từ `origin/main`. PR vào `main`; `develop` không còn cập nhật từ 15/10/2026.
- Commit tiếng Việt, có `Closes #<issue>`. Người phụ trách mở và merge PR.

## 11. Bẫy đã gặp

- **Schema drift:** DB test dựng từ migration, còn prod có ràng buộc dựng tay từ baseline Supabase cũ. Ví dụ `users_check` gây 500 ở #237. Nghi drift thì so với prod (chỉ đọc).
- **Trigger khoá nội dung:**
  - Hạ tầng Approved và review đã quyết không sửa được, kể cả bằng SQL.
  - Muốn sửa phải tạo phiên bản mới.
  - Muốn gắn thêm dữ liệu cho chúng, dùng bảng riêng (như `expert_review_assignments`).
- **Thứ tự khoá thẩm định:** khoá công trình → `fire3d:expert-assignment`. Không gọi `expert_assign_pending` trước khi giữ khoá công trình trong đường ghi.
- **`paid_at` bất biến:** test giả lập tiền về trễ phải tắt trigger tạm (`session_replication_role=replica`).
- **Kỳ dịch vụ v7:** tiền về sau `startsAt` thì kỳ dời sang `paidAt` (#241). Báo giá phải cách giờ bắt đầu ≥ 15 phút và tổng ≥ 2.000 ₫.
- **Bản nháp tình huống trên hạ tầng:** không chứa Goal hay thiết bị cố định. Validate và snapshot gộp với item của hạ tầng.

## 12. Thay đổi gần đây (để hiểu vì sao code như hiện tại)

| Issue | Điểm chính |
|---|---|
| #211 | Vai trò FireSafetyExpert, hàng chờ thẩm định |
| #213 | Hạ tầng PCCC theo revision, khoá sau thẩm định |
| #215–#219 | Hồ sơ PC06, số bước và đường ngắn nhất, nhật ký luyện tập, chặn IFC rác |
| #224 | Trạng thái cửa (khoá, chèn) là đối tượng của tình huống |
| #231 | Bản nháp AI ghim hạ tầng |
| #235 | Chuyên viên đăng nhập lần đầu: mã email, rồi đổi mật khẩu |
| #237 | Xoá ràng buộc `users_check` cũ trên prod |
| #239 | Phiên bản 1 hạ tầng: bỏ nhãn lớp IFC, Goal `exit.final`; `previousAttempt` / `endReason` |
| #241 | PayOS tiền về trễ thì dời kỳ; báo giá đệm 15 phút, tối thiểu 2.000 ₫ |
| #242 | Phân công thẩm định xoay vòng, quản trị gán lại |
| #245 | Quản trị không còn duyệt (410), Chuyên viên cũ được giao việc, một thứ tự khoá, Chuyên viên bị khoá trả việc |
