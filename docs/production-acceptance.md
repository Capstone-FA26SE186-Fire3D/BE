# Nghiệm thu production liên dịch vụ (#92)

Mỗi dịch vụ một dòng, với bằng chứng thật (ngày, lệnh, kết quả). Test có fake (S3, worker, PayOS mock) không tính là đạt. Bảng
được cập nhật mỗi lần deploy; dòng chưa có bằng chứng giữ trạng thái "Chưa".

Môi trường: API `https://api.fet3d.io.vn` (cùng App Service `https://fire3d-api-h3a5h2fgdvajfbcz.eastasia-01.azurewebsites.net`), Supabase PostgreSQL 17.

## Cập nhật 17/10/2026

| Hạng mục | Trạng thái | Bằng chứng / việc tiếp theo |
|---|---|---|
| Tạo Building trên production | Đạt sau sửa | 17/10 smoke test: `POST /api/buildings` có `Idempotency-Key` trả **500**. Nguyên nhân (chỉ đọc): Supabase còn **73 trigger tạo tay** từ script SQL Docs v7, không do migration nào tạo; trigger `buildings.core_audit` ghi `audit_logs` dùng default `extensions.gen_random_uuid()` mà không role app nào có quyền USAGE trên schema `extensions`. Building cuối cùng được tạo ngày 03/10. Các trigger khác (`validate_owned_content`, `enforce_revision_status_transition`, `update_updated_at_column`, …) cũng chặn revision/scenario/session; production chưa có revision/scenario/session/release nào nên không mất dữ liệu. Sửa bằng migration `20261017100000_AlignProductionSchema` (branch `fix/align-production-schema`): xóa đúng 73 trigger đó, cấp USAGE `extensions` cho role `fire3d_*`/`fet3d_*`. Đã backup rồi áp lên Supabase (113 migration); kiểm tra đọc: còn 0 trigger tạo tay, 3 trigger PayOS của migration vẫn còn, readiness Healthy. Smoke lại: POST Building 201. 24/10: 21 hàm trigger v7 mồ côi còn lại đã được xóa bằng `20261024110000_DropLegacyV7Functions`. |
| Smoke test API mới (QA org riêng) | Đạt 34/37 | 17/10 trên `b5b328e`: Building (#94) tạo/replay/ETag/412; IFC-03 (`.rvt` 400, `.ifc` 201, issues, bim-facts, equipment-candidates 409/404 tenant khác); STYLE-01 (preset/scheme publish, màu sai 422, GET/PUT style, 428/412/404); training buildings, AI quota/limits, runtime-catalog, OpenAPI. 3 mục "thiếu header → 400/428" không đạt vì production đang bật `Compatibility__BuildingHeadersOptional=true` (DELETE thiếu `If-Match` trả 200 kèm `Warning`); tắt cờ khi FE#48 xong. Dữ liệu QA đã tắt/archive. |
| Quét toàn bộ route (chỉ đọc) | Đạt | 17/10: 269 operation trong OpenAPI. Không token: 237 trả 401, 3 trả 404 hợp lệ (slug Learn ngẫu nhiên, route `{operation}` nhận GUID), 25 route anonymous/internal không gọi ghi. Mọi GET với token PlatformAdmin, OrganizationUser QA, Trainee QA: **không có 5xx**. |
| Outbox | Có dữ liệu | 17/10: 6 sự kiện, cả 6 `Published`, không Pending. |

## Trạng thái 16/10/2026

| Dịch vụ | Trạng thái | Bằng chứng / việc tiếp theo |
|---|---|---|
| Health, version, readiness | Đạt (bản `main`) | 16/10 sau deploy PR #97/#104/#105/#106: `GET /health/version` = `4606fe9` (HEAD `main`); `GET /health/ready` 200 `{"status":"Healthy","checks":{"database":{"status":"Healthy"}}}`; `GET /openapi/v1.json` 200, có `RevisionIssueResponse.ifcGlobalIds` và mã `EVENT_TYPE_UNSUPPORTED`. Làm lại sau mỗi deploy. |
| Schema Supabase | Đạt tới `20261016090000` | 16/10: 109 migration, mới nhất `20261016090000_AddTrainingEventAllowList`; backup `pg_dump -n public` trước khi áp. Kiểm tra đọc: có `bim_facts`, `revision_equipment_decisions`, `fire3d_api` có EXECUTE `revision_equipment_gate`, `anon` không đọc được `bim_facts`. DB đang đi trước code deploy; readiness vẫn Healthy, và cả 33 hàm SQL mà code `main` gọi vẫn có đúng số tham số (kiểm tra đọc 16/10). Sau khi áp `20261016080000` và `20261016090000`: cột `rejection_code` có trên `session_events`/`playtest_events`, `fire3d_api` chỉ có EXECUTE trên hai gate (không trên hàm phụ), owner các gate là `fet3d_ifc_upload_owner`, readiness vẫn Healthy. |
| API mới đã deploy | Đạt | 16/10 trên `4606fe9`, gọi không token đều 401 (route tồn tại, không phải 404): `GET /api/ai/trainee/quota`, `GET /api/ai/organization/limits`, `GET /api/training/buildings`, `GET /api/revisions/{id}/equipment-candidates`, `PATCH /api/revisions/{id}/equipment-decisions`, `GET /api/revisions/{id}/bim-facts`, `POST /api/buildings`. FE chưa gửi `Idempotency-Key`/`If-Match` cho Building (FE#48) và còn dùng status AI cũ (FE#56): theo dõi ở #93. |
| Outbox, dispatcher, cache invalidation | Chưa có dữ liệu | 16/10 truy vấn chỉ đọc `integration_outbox_events`: 0 dòng, 0 Pending quá 15 phút. Làm lại sau khi có upload IFC, nhắc hết hạn hoặc sửa catalog trên production. |
| Email OTP và quên mật khẩu (Mailgun) | Chưa | Cần người làm: đăng ký và quên mật khẩu bằng email thật, chụp email nhận được, ghi thời gian. Nếu không nhận: kiểm App Settings `Mailgun__*` trên Azure. |
| Nhắc hết hạn 5 ngày | Chưa | Cần PlatformAdmin: cấp Trial cho một Building thử (`POST` Trial, hết hạn trong 5 ngày), bật worker nhắc, kiểm `GET /api/organizations/me/notifications` và email. Thao tác ghi dữ liệu production, cần người duyệt. |
| PayOS tạo link, webhook, đối soát, cấp gói | Chưa | Tốn tiền thật: báo giá nhỏ, trả qua QR theo `docs/publish-billing-v7-manual-test.md`, kiểm webhook đã nhận và entitlement được cấp. |
| Learner session, playtest | Một phần | Cần đọc App Settings `LearnerSessions__Enabled`, `Playtest__Enabled` trên Azure (máy này không có Azure CLI). Launch thật cần client Unity. |
| Worker IFC, Blender, Unity | Bị chặn | Chờ worker thật (Unity#6). |
| FCM push | Bị chặn | Chờ app Mobile (Mobile#13). |
| Adapter AI thật | Bị chặn | Chờ service AI theo adapter v2 (AI#2, AI#8). |

## Cách làm lại phần chỉ đọc

- Health: `curl -s <API>/health/ready` và `<API>/health/version`, so commit với `git log -1 origin/main`.
- Migration: `SELECT count(*), max("MigrationId") FROM "__EFMigrationsHistory";` với `PGOPTIONS='-c default_transaction_read_only=on'`.
- Outbox: `SELECT status, count(*), min(created_at) FROM integration_outbox_events GROUP BY 1;` và đếm Pending quá 15 phút.

Không ghi secret, token hay connection string vào file này. #92 chỉ đóng khi mọi dòng bị chặn đã nghiệm thu hoặc có quyết định bỏ.
