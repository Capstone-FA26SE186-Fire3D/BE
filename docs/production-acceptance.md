# Nghiệm thu production liên dịch vụ (#92)

Mỗi dịch vụ một dòng, với bằng chứng thật (ngày, lệnh, kết quả). Test có fake (S3, worker, PayOS mock) không tính là đạt. Bảng
được cập nhật mỗi lần deploy; dòng chưa có bằng chứng giữ trạng thái "Chưa".

Môi trường: API `https://fire3d-api-h3a5h2fgdvajfbcz.eastasia-01.azurewebsites.net`, Supabase PostgreSQL 17.

## Trạng thái 16/10/2026

| Dịch vụ | Trạng thái | Bằng chứng / việc tiếp theo |
|---|---|---|
| Health, version, readiness | Đạt (bản `main`) | 16/10 04:32 UTC: `GET /health` 200; `GET /health/ready` 200 `{"status":"Healthy","checks":{"database":{"status":"Healthy"}}}`; `GET /health/version` = `5a91f7a` (HEAD `main`, PR #99); `GET /openapi/v1.json` 200. Làm lại sau mỗi deploy. |
| Schema Supabase | Đạt tới `20261015092000` | 16/10: 107 migration, mới nhất `20261015092000_AddIfcQaContract`; backup `pg_dump -n public` trước khi áp. Kiểm tra đọc: có `bim_facts`, `revision_equipment_decisions`, `fire3d_api` có EXECUTE `revision_equipment_gate`, `anon` không đọc được `bim_facts`. DB đang đi trước code deploy; readiness vẫn Healthy, và cả 33 hàm SQL mà code `main` gọi vẫn có đúng số tham số (kiểm tra đọc 16/10). Chưa áp: `20261016080000` (sửa lỗi review IFC) và `20261016090000` (SCENARIO-02), áp khi merge branch tương ứng. |
| API của PR chưa merge | Chưa | `GET /api/ai/trainee/quota` 404 trên production (PR #97 chưa deploy). Sau mỗi lần merge: kiểm `/health/version` đúng commit và gọi route mới không token → 401 (không phải 404). |
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
