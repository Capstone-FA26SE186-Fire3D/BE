# Fire3D → Supabase: migration chuẩn bị cho project mới

Bộ này tạo schema sạch, không upload dữ liệu local, tài khoản, password hash, JWT hoặc refresh token. Chưa áp dụng lên project Supabase thật. PostgreSQL 17+ là điều kiện của baseline này.

## Phạm vi

- 37 bảng, 30 function: 4 module SQL v6 từ Docs/database và BE/database/001_auth_refresh_tokens.sql.
- Giữ enum, FK, CHECK, index, trigger, snapshot/audit invariants và ranh giới PayOS; Phase 2 schema không đồng nghĩa feature Phase 2 đã triển khai.
- Source hashes/object manifest: source-manifest.json. Không sử dụng Docs/fire_evacuation_schema.sql vì file hiện tại chỉ chứa thông báo chạy SQL.
- Auth vẫn do Fire3D BE quản lý ở public.users; không chuyển sang Supabase Auth/auth.users.
- Không reset DB, không có DROP schema/table hoặc tự chạy migration khi API khởi động.

## Điều chỉnh cho managed PostgreSQL

1. pgcrypto ở extensions nếu chưa cài; nếu đã tồn tại thì giữ vị trí hiện có. Function invoker có search_path cố định pg_catalog, public, extensions, pg_temp; hai function PayOS SECURITY DEFINER giữ search_path pg_catalog.
2. Role migration cần CREATEROLE và quyền tạo object. Không cần SUPERUSER. Để chuyển ownership ledger, role migration nhận membership của các role PayOS; quyền CREATE schema cấp tạm cho ledger owner rồi thu hồi. Runtime không nhận các membership này.
3. Thu hồi quyền trên từng object Fire3D từ PUBLIC/anon/authenticated/service_role; không REVOKE toàn bộ object trong public. Thu hồi CREATE public từ PUBLIC để bảo vệ search_path.
4. Bật RLS trên toàn bộ bảng ứng dụng, không tạo policy cho client roles. Nhóm fire3d_api chỉ được SELECT/INSERT/UPDATE users, organizations, auth_refresh_tokens và INSERT audit_logs. Runtime không được DELETE user, UPDATE/DELETE audit hoặc ghi ledger.
5. Policy runtime cho phép truy cập các hàng thuộc phạm vi backend tin cậy; đây KHÔNG phải RLS tenant theo JWT người dùng. BE phải kiểm tra role/organization cho từng request. Khi thêm module mới, bổ sung grants/policies bằng migration riêng, không cấp ALL TABLES.

## Trước khi áp dụng

- Dùng project Supabase dev/staging mới, kiểm tra PostgreSQL >=17 và chưa có schema Fire3D hoặc custom roles trùng tên.
- Tắt Data API nếu chỉ dùng Npgsql qua BE. Không cấu hình FE/mobile truy cập bảng hoặc RPC Fire3D trực tiếp.
- Lấy connection thông qua Dashboard → Connect: direct khi mạng hỗ trợ, hoặc session pooler port5432 cho IPv4. Không dùng transaction pooler cho migration.
- Dùng tài khoản migration của project; mật khẩu nhập qua prompt hoặc secret manager, không thêm vào file/commit/command history.
- Không import toàn bộ dump role của PostgreSQL local; không dùng db reset trên remote.

## Áp dụng bằng psql (phương án chính)

Mở terminal tại thư mục supabase này. Khai báo PGHOST, PGPORT, PGUSER, PGDATABASE theo Connect của project đích; dùng SSL và kiểm tra certificate theo môi trường (ưu tiên verify-full). Không ghi PGPASSWORD vào script. psql sẽ hỏi password nếu chưa có credential an toàn.

```powershell
psql -X -W --set=ON_ERROR_STOP=1 --single-transaction --file=migrations/20260916000100_fire3d_baseline.sql
# Chỉ chạy lệnh sau nếu lệnh trước exit code 0.
psql -X -W --set=ON_ERROR_STOP=1 --file=verify.sql
```

Giữ working directory ở supabase và dùng đường dẫn tương đối; psql trên Windows có thể xử lý sai đường dẫn tuyệt đối có dấu tiếng Việt.

Baseline không tự có BEGIN/COMMIT vì runner quản lý transaction. Không chạy riêng từng đoạn. Nếu dùng SQL Editor, bọc toàn bộ file trong BEGIN; ... COMMIT; và chạy tất cả một lần. Lỗi phải rollback, không tiếp tục các bước sau.

Tên migration cũng phù hợp thư mục Supabase CLI migrations. Nếu team chọn CLI, cần khởi tạo/cấu hình CLI, xác nhận project link, kiểm tra db push --dry-run trước khi push. Chỉ chọn một cơ chế quản lý lịch sử: áp dụng thủ công bằng psql không tự ghi lịch sử supabase_migrations. Không chạy CLI push lại cùng baseline đã áp dụng thủ công; cần đối chiếu và đánh dấu lịch sử có chủ đích.

Chạy lại baseline bị chặn ngay khi phát hiện bảng/role đã tồn tại. Muốn nâng cấp DB có dữ liệu phải viết migration bổ sung, không sửa/ép chạy bootstrap.

## Tạo login runtime cho BE

Sau khi baseline và verify đạt:

```powershell
psql -X -W --set=ON_ERROR_STOP=1 --single-transaction --file=provision-backend.sql
# Trong phiên psql của migration owner, đặt password bằng prompt:
# \password fire3d_backend
```

Không dùng postgres/migration owner hoặc ledger owner làm login thường trực của API. Không cấp fire3d_api cho anon, authenticated, service_role. Với session pooler, username custom role có dạng fire3d_backend.<project-ref>; kiểm tra định dạng hiện hành trong Connect/tài liệu Supabase.

ConnectionStrings:DefaultConnection của BE chứa host/port/database/username/password runtime qua User Secrets (local) hoặc Secret Manager (cloud), bật SSL và giới hạn pool phù hợp. Đây là PostgreSQL connection string, không phải Supabase URL + anon key. Không thay cấu hình local hiện tại trước khi kiểm thử staging xong.

Bootstrap admin staging theo docs/authentication.md bằng CLI BE, không seed admin/password vào migration. Dùng JWT SigningKey riêng cho staging. Kiểm tra login/me/refresh/logout, account/org administration, 401/403, thu hồi phiên và audit trước khi FE chuyển API URL.

## Kiểm thử đã thực hiện

Trên PostgreSQL17 local, DB và role tạm riêng, không thay schema/dữ liệu DB ứng dụng:

- Apply toàn baseline bằng role NOSUPERUSER CREATEROLE: đạt.
- Bộ SQL invariant core và Phase 2 từ Docs: đạt.
- Mô phỏng default grants của client roles; sau migration không còn quyền đọc users/refresh hoặc gọi payment RPC.
- RLS trên mọi bảng Fire3D; quyền đọc object managed giả lập không bị thu hồi.
- Role runtime thực hiện được ghi user/org/session/audit và đọc tài khoản; test transaction rollback.
- Runtime không được ghi payment ledger, DELETE users hoặc UPDATE audit.
- Chạy baseline lần hai bị từ chối ở preflight.

Lệnh tái lập từ gốc worktree BE:

```powershell
powershell -NoProfile -File supabase/test-local.ps1
```

Script dùng credential local hiện có trong API User Secrets, chỉ chấp nhận localhost; cần quyền tạo DB/role tạm để mô phỏng môi trường. Mọi tên DB/role kiểm thử có suffix ngẫu nhiên và được dọn sau test. Không dùng script này với Supabase hosted.

## Chưa xác minh và phục hồi

Chưa kiểm thử Supabase hosted/supautils, Supabase CLI, network/TLS/pooler hay login Npgsql trên staging thật. Kết quả local không bảo đảm mọi policy managed đều tương đương.

Nếu apply lỗi: transaction rollback; lưu lỗi đã che secrets, sửa compatibility rồi thử trên staging mới. Sau khi apply thành công và có dữ liệu, rollback bằng backup/restore đã xác minh hoặc migration sửa tiến; không có down migration phá dữ liệu.

## Nguồn chính thức

- https://supabase.com/docs/guides/database/postgres/roles-superuser
- https://supabase.com/docs/guides/database/connecting-to-postgres
- https://supabase.com/docs/guides/api/securing-your-api
- https://supabase.com/docs/guides/deployment/database-migrations

Để tái sinh migration trước khi phát hành (cần sibling Docs chính xác): python supabase/build_baseline.py --docs <Docs-directory>. Không tái sinh/chỉnh file migration đã áp dụng ở môi trường dùng chung; tạo migration mới.