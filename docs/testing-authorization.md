# Test phân quyền trên Visual Studio / Swagger

> Hướng dẫn này áp dụng cho database local của implementation auth legacy để thử API hiện có; nó dùng schema v6 và script trong BE, không phải quy trình khởi tạo schema sản phẩm mới. Với contract đích, đọc [Docs schema/ERD](../../Docs/fire_evacuation_schema.sql) và [checklist BE](api-implementation-checklist.md). Chỉ chạy trên database disposable riêng; không trỏ Swagger vào môi trường dùng chung.

## 1. Chuẩn bị

- Chọn Fire3D.API làm Startup Project, chọn profile **https**, nhấn F5 hoặc Ctrl+F5.
- Swagger theo launchSettings hiện tại: https://localhost:7028/swagger.
- Database phải có schema v6 và bảng auth_refresh_tokens từ database/001_auth_refresh_tokens.sql. Chỉ chạy script bổ sung này một lần nếu chưa có bảng; không chạy lại bootstrap toàn database.
- Cần ConnectionStrings:DefaultConnection và Jwt:SigningKey trong Manage User Secrets. Nếu chưa có PlatformAdmin, làm bước bootstrap trong [authentication.md](authentication.md). Không bootstrap lại để đổi mật khẩu admin đã có.
- Các thao tác Swagger tạo dữ liệu thật trong database đã cấu hình. Dùng email/slug thử riêng và ghi lại ID để phân biệt.

## 2. Ma trận quyền

| Thao tác | Chưa login | PlatformAdmin | OrganizationUser | Trainee |
|---|---|---|---|---|
| Login / refresh | Được gọi; credentials phải hợp lệ | Được gọi | Được gọi | Được gọi |
| GET /api/auth/me, POST /api/auth/logout | 401 | Chính mình | Chính mình | Chính mình |
| Tạo/list/detail/khóa organization | 401 | Được | 403 | 403 |
| Tạo/list/detail/khóa account | 401 | Được | 403 | 403 |

Controller mặc định yêu cầu đăng nhập. Policy PlatformAdministration yêu cầu PlatformAdmin; các handler quản trị còn kiểm tra actor từ database. API lấy actor từ token, không từ body/query. OrganizationUser không có quyền xem danh sách account/organization kể cả của tổ chức mình trong module quản trị này.

## 3. Tạo dữ liệu bằng admin

1. Gọi POST /api/auth/login với email/password admin. Kết quả 200 có accessToken và refreshToken.
2. Bấm **Authorize**, dán riêng accessToken (không thêm chữ Bearer), bấm Authorize rồi Close.
3. POST /api/organizations:

```json
{ "name": "Organization test", "slug": "organization-test" }
```

Kỳ vọng 201; lưu id thành ORG_ID. Nếu slug đã có thì đổi slug; trùng trả 409.

4. POST /api/accounts để tạo OrganizationUser:

```json
{
  "email": "manager-test@example.com",
  "password": "<mật khẩu thử riêng, từ 12 đến 128 ký tự>",
  "fullName": "Manager Test",
  "role": "OrganizationUser",
  "organizationId": "<thay bằng ORG_ID thật>"
}
```

Kỳ vọng 201. Lưu id và mật khẩu bạn đã chọn. Tạo tiếp một Trainee với email khác, role Trainee và organizationId null.

5. GET /api/organizations và GET /api/accounts: admin nhận 200. Thử page=1, pageSize=20; accounts có thêm role, organizationId, isActive, search. Không có passwordHash/token trong dữ liệu trả về.

## 4. Kiểm tra đúng/sai quyền

1. Trong Authorize bấm Logout để xóa token khỏi Swagger. Đây chỉ là xóa token ở UI, không phải endpoint logout.
2. GET /api/accounts: kỳ vọng 401.
3. Login bằng OrganizationUser, thay access token trong Authorize. GET /api/auth/me trả đúng tài khoản đó; GET /api/accounts hoặc /api/organizations trả 403. Thử cả GET theo ID của tài nguyên đã tạo: vẫn 403.
4. Lặp lại bằng Trainee: me trả 200, endpoint quản trị trả 403.
5. Đăng nhập admin lại để thực hiện bước khóa bên dưới.

Không dùng refresh token trong ô Authorize. Mỗi lần đổi user phải thay access token; chỉ gọi login không tự đổi token đang được Swagger dùng.

## 5. Khóa/mở khóa và token cũ

1. Login Trainee, lưu accessToken và refreshToken của Trainee, rồi đặt lại access token admin trong Authorize.
2. PATCH /api/accounts/TRAINEE_ID/status:

```json
{ "isActive": false }
```

Kỳ vọng 200. Login Trainee bị 401. Dùng access token Trainee cũ gọi me: 401.

3. Đặt lại token admin, PATCH cùng đường dẫn với isActive true: 200.
4. Access/refresh token Trainee cũ vẫn bị 401 sau mở khóa; login lại mới nhận token dùng được.
5. Với organization, gọi PATCH /api/organizations/ORG_ID/status. Khóa sẽ chặn OrganizationUser và thu hồi phiên của họ; mở lại cần login mới. Trainee không thuộc organization nên không bị khóa theo tổ chức.
6. Admin PATCH chính ACCOUNT_ID của mình với isActive false: 409/SELF_DEACTIVATION. Thiếu isActive: 400. ID không tồn tại: 404.

## 6. Refresh và logout

- POST /api/auth/refresh với body { "refreshToken": "<token mới nhất>" }: 200, thay cả access và refresh token.
- Reuse refresh token cũ: 401, thu hồi cả phiên đó. Token vừa rotate từ cùng phiên cũng không dùng tiếp được; cần login lại. Thử trường hợp này cuối lượt test để không vô tình làm hỏng các bước trước.
- POST /api/auth/logout với access token hợp lệ: 204. Dùng lại access token đó gọi me: 401.

Nếu nhận 429, đã chạm giới hạn request; đợi hết cửa sổ một phút rồi thử lại. Login/refresh/create-account dùng chung mức 10 request/IP/phút; quản trị còn lại 120 request/IP/phút. Đừng nhầm 429 với lỗi phân quyền.

## 7. Chạy test tự động

Trong **View → Terminal**, đứng tại thư mục BE:

```powershell
dotnet build Fire3D/Fire3D.slnx
$env:FIRE3D_TEST_USE_LOCAL_SECRETS = "1"
try {
    dotnet test Fire3D/Fire3D.AuthTests/Fire3D.AuthTests.csproj --no-build --no-restore
} finally {
    Remove-Item Env:FIRE3D_TEST_USE_LOCAL_SECRETS
}
```

Test dùng PostgreSQL localhost và credentials từ API User Secrets có quyền CREATEDB; cần repo Docs nằm cạnh BE. Mỗi test tạo database fire3d_auth_test_<random>, xác minh kết nối trỏ vào database đó rồi mới ghi dữ liệu, và xóa database sau test. Không chạy vào dữ liệu ứng dụng. Có thể dùng FIRE3D_TEST_ADMIN_CONNECTION cho PostgreSQL test riêng; không commit connection string.

Nếu không bật cấu hình test thì các test PostgreSQL bị Skip, không phải Pass. Xem thêm cấu hình trong [authentication.md](authentication.md).
