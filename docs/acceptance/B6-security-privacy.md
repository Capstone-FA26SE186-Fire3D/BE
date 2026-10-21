# Nghiệm thu B6 (BE#141): bảo mật, privacy và public ingress

Phần BE: phân quyền theo role/tenant/DB, least privilege của worker và AI, S3 riêng tư, kiểm tra upload, log không lộ dữ liệu.
Phần hạ tầng (OneShield/OnePortal → Nginx → API, TLS/DNS/WAF) do nhóm vận hành chốt theo Docs; BE chỉ cung cấp bằng chứng phía API.

## 1. Bằng chứng tự động

Cách chạy như [B1](B1-identity.md#1-bằng-chứng-tự-động); login runtime bị giới hạn (`FIRE3D_TEST_RUNTIME_LOGIN=1`) nên test chạy với
đúng quyền DB như production. Kết quả 21/10/2026, `main@82f8eb8`: AuthTests 731/731, IfcTests 154/154.

| Mục checklist | Test tự động |
|---|---|
| Chéo tenant/role/user bị chặn; client không có quyền DB | `BuildingTenantIsolationTests.Building_routes_hide_other_tenants_and_reject_edits_after_archive`, `BuildingMutationContractTests.Organization_user_cannot_select_another_tenant_even_when_calling_handler_directly`, `BillingApiTests.Catalog_permissions_validation_and_cross_tenant_quotation_are_enforced`, `AuthorizationIntegrationTests.Signed_tokens_cannot_override_database_role_organization_or_session_owner`, `AuthorizationIntegrationTests.Controllers_require_auth_by_default_and_anonymous_management_requests_are_rejected`, `ExitDoorConfirmationTests.Client_roles_have_no_privileges_on_public_relations` |
| Worker/AI least privilege; client không giữ service credential | `PackageWorkerTests.Package_lease_endpoint_needs_the_machine_key_and_pull_mode`, `GeometryWorkerTests.Geometry_lease_endpoint_needs_the_machine_key_and_pull_mode`, `AuthIntegrationTests.Worker_requests_report_an_executor_login_without_the_role_as_503_and_health_worker_shows_it`, `AiContractV2Tests.Knowledge_chunks_are_created_with_pgvector_and_only_the_indexer_role_uses_them`, `OrganizationAiPostgresTests.Organization_ai_rejects_foreign_citations_and_usage_beyond_reservation_and_retries_undelivered_calls` |
| S3 riêng tư, URL ký có hạn; upload kiểm type/size/hash | `AvatarS3AdapterTests.Private_download_url_contains_s3_signature_and_needs_no_api_bearer`, `AvatarServiceTests.Create_upload_intent_uses_a_server_owned_staging_key_and_a_five_minute_url`, `AvatarServiceTests.Complete_rejects_oversized_dimensions_before_copy`, `IfcUploadRecoveryPostgresTests.*`, `PackageWorkerUnitTests.*` (checksum SHA-256 khi PUT) |
| Log/telemetry/export/audit không lộ secret hoặc dữ liệu người khác | `AuditReportingTests.Audit_projection_excludes_secrets_unknown_actions_and_unsafe_values`, `AuthDiagnosticsTests.Login_measurements_have_only_bounded_phase_labels`, `MailgunEmailServiceTests.Accepted_response_completes_without_exposing_the_message_body`, `GoogleExchangeHttpTests.Anonymous_exchange_preserves_error_status_and_safe_problem_details` |
| Rate limit API | `AuthIntegrationTests.Refresh_requests_are_rate_limited_before_invalid_token_processing`, `AnonymousAuthHttpTests.Registration_otp_rate_limit_returns_a_positive_retry_after_header`, `AdministrationRateLimitTests.Administration_limit_returns_problem_details_with_code_and_retry_after` |
| Cache không cấp quyền | `PackageWorkerTests.Learner_package_files_are_downloadable_after_prepare_without_using_a_seat`, `LearnerSessionsPostgresTests.Learner_continuation_syncs_after_logout_and_loss_of_access_but_never_starts` |

Quét toàn bộ route production (19/10/2026, 276 endpoint, 3 loại token và không token): route cần đăng nhập trả 401/403 khi không
token, không endpoint nào trả 5xx.

## 2. Thử tay

| # | Bước | Kỳ vọng |
|---|---|---|
| 1 | Đăng nhập tổ chức A, gọi `GET /api/buildings/{id}` với id tòa của tổ chức B | 404 (không lộ là tồn tại) |
| 2 | Trainee gọi API quản trị (`GET /api/accounts`, `POST /api/releases`) | 403 |
| 3 | Sửa JWT (đổi role/org trong payload, giữ chữ ký cũ) | 401 |
| 4 | Gọi `/internal/processing/package-jobs/lease` không có hoặc sai `X-Worker-Key` | 401 |
| 5 | Mở trực tiếp URL S3 của file (không có chữ ký) | 403 từ S3; URL có chữ ký hết hạn sau thời gian ghi trong response |
| 6 | Upload avatar 10 MB / file `.exe` đổi đuôi thành `.png` | 400, không lưu |
| 7 | Upload IFC rồi gọi `upload-complete` với SHA-256 sai | Bị từ chối, revision không chuyển xử lý |
| 8 | Gửi 100 request đăng nhập sai liên tiếp | 429 có `Retry-After` |
| 9 | Kiểm Azure Log stream và export analytics/audit | Không có password, token, OTP, khóa API, email người khác |
| 10 | Kiểm lớp ingress: `curl -I http://api.fet3d.io.vn` và `https://` | HTTP chuyển sang HTTPS; header bảo mật theo cấu hình Nginx/WAF |
| 11 | Kiểm DB: `SELECT has_table_privilege('anon','users','SELECT')` (Supabase SQL editor) | `false` cho mọi bảng nhạy cảm |

## 3. Mẫu báo cáo bảo mật vào BE#141

```text
Ngày (UTC), BE <sha>, phạm vi: API, DB grants, S3, log, ingress
Bảng: bước 1..11 -> OK/FAIL, ảnh/response (đã che dữ liệu)
Finding: mức độ, mô tả, cách tái hiện, PR sửa, ngày retest
Chính sách lưu/xóa dữ liệu tối thiểu: <tham chiếu Docs>
```

## 4. Không thuộc BE

Chọn và vận hành OneShield/OnePortal, TLS/DNS/WAF, chính sách lưu trữ dữ liệu là quyết định vận hành và Docs.
