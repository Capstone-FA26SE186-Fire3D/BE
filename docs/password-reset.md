# Forgot/reset password: triển khai và kiểm tra

## API hiện tại

- `POST /api/auth/forgot-password`, anonymous: `{ "email": "user@example.com" }`.
  Email được trim/lowercase, kiểm tra định dạng và tối đa 254 ký tự. Thành công trả
  `202` với `{ "message": "..." }`, kể cả email không có tài khoản. Đây là xác nhận
  nhận yêu cầu; không chứng minh Mailgun đã gửi email. Email sai trả `400`.
- `POST /api/auth/reset-password`, anonymous:
  `{ "oobCode": "<mã trong email>", "newPassword": "<12–128 ký tự>" }`.
  Thành công `204`; dữ liệu/mã sai `400`; reset đang được ghi nhận `409`;
  provider không khả dụng `503`; rate limit `429`.
- Chỉ tài khoản có email/password trên Firebase và UID khớp hồ sơ Fire3D mới được
  reset. Tài khoản chỉ dùng Google không được chuyển sang password bằng luồng này.

## Database và thứ tự deploy

Chạy `database/002_password_reset_recovery.sql` đúng một lần trước khi deploy bản
code dùng nó. Script thêm `password_reset_email_jobs` và `password_reset_operations`;
không xóa bảng cũ. Mọi lần cấp refresh token đều kiểm tra bảng operations, nên
thiếu migration có thể làm login/refresh lỗi ngay cả khi chưa gọi reset.

Script được kiểm thử trên PostgreSQL local riêng. Chưa áp dụng lên Supabase trong
task này. EF `EnsureCreated` hoặc các migration EF hiện có không thay thế script này.
Nếu backend dùng role riêng, cấp quyền SELECT/INSERT/UPDATE và policy RLS cho đúng
role; script hỗ trợ sẵn `fet3d_backend_executor` nếu role này tồn tại.

Queue reset mới không đọc các event PasswordReset cũ trong integration outbox.
Kiểm tra các event cũ trước triển khai; không cho hai worker cùng gửi chúng.

## Cấu hình

Đặt qua User Secrets hoặc biến môi trường, không commit giá trị thật:

| Biến | Giá trị cần cung cấp |
|---|---|
| `AuthEmail__FrontendUrl` | HTTPS origin/base path của web; localhost HTTP dùng cho dev |
| `AuthEmail__FirebaseApiKey` | Firebase Web API key đúng project |
| `AuthEmail__WorkerEnabled` | `true`; dùng `false` để tạm ngừng worker |
| `Mailgun__ApiKey` | API key của Mailgun |
| `Mailgun__Domain` | Sending domain đã cấu hình |
| `Mailgun__From` | Địa chỉ người gửi |
| `Mailgun__BaseUrl` | Endpoint Mailgun của region đang dùng |

Firebase Admin ưu tiên `Firebase__ServiceAccountJson`, sau đó section `FirebaseAdmin`,
sau đó file local `firebase-admin.json`. Section mẫu đã được bỏ khỏi appsettings vì
private key mẫu khiến ứng dụng không khởi động dù file local hợp lệ. Muốn dùng
section, cấu hình đủ service-account fields và PEM private key; không dùng đồng
thời nhiều nguồn cấu hình không thống nhất. Cấu hình sai rõ ràng phải được sửa,
không được coi là xác thực Firebase thành công.

Web cần trang `/reset-password`, đọc `oobCode` từ query và gọi API reset của BE.
Không gọi Firebase confirm trực tiếp từ trang này vì sẽ bỏ qua thu hồi phiên Fire3D.
Không đưa password, code hay link reset vào log/analytics/referrer.

## Gửi email và retry

Queue giới hạn một job/email trong một phút. Worker nhận lease hai phút, giới hạn
công việc 45 giây, tối đa 5 lần thử và backoff. Lease token ngăn worker cũ xác nhận
job đã được worker khác nhận lại. Job lỗi vĩnh viễn hoặc hết lượt chuyển `Dead`.
Đây là gửi ít nhất một lần: nếu Mailgun nhận email nhưng worker chết trước khi ack,
người dùng có thể nhận email lặp. Theo dõi Pending/Dead và thời gian chờ; lên lịch
xóa job đã hoàn tất theo chính sách lưu giữ email của dự án.

## Reset và phục hồi lỗi giữa chừng

BE ghi operation `Pending` và thu hồi mọi refresh-token family trong transaction
ngắn trước khi gọi Firebase. Không giữ transaction DB khi chờ mạng. Khi Firebase
thành công, BE ghi `Completed`, thu hồi lại các phiên và ghi audit. Lỗi 400 xác định
ghi `Rejected`. Timeout/cancellation hoặc lỗi commit cuối giữ `Pending`, chặn cấp
phiên mới; phiên cũ đã bị thu hồi. Login xác thực trước mốc hoàn tất phải đăng nhập lại.

Hiện chưa có job tự đối soát kết quả Firebase không rõ. Cách xử lý vận hành:

1. Tìm operation Pending bằng id/user_id, kiểm tra request/log theo correlation;
   không suy luận thành công chỉ từ timeout hoặc tuổi bản ghi.
2. Quản trị viên xác minh chủ tài khoản, UID và trạng thái Firebase, thực hiện
   quy trình khôi phục để xác lập mật khẩu/trạng thái đã biết và revoke Firebase
   sessions. Không xóa fence khi vẫn có request confirm đang chạy.
3. Chỉ sau khi xác định kết quả, chạy `IPasswordResetStore.FinishAsync` bằng tác vụ
   nội bộ được kiểm soát để ghi Completed/Rejected, revoke phiên và audit trong
   cùng transaction. Không sửa status trực tiếp để bỏ qua audit/revocation.
4. Người dùng đăng nhập lại. Chưa có API public/admin hay CLI cho thao tác nội bộ
   ở bước 3; cần thực hiện qua công cụ vận hành có DI của ứng dụng.

Mã đã được ghi nhận không được dùng lại; sau Rejected cần xin email reset mới.
Không lưu plaintext code/password trong operation; code chỉ được lưu dạng SHA-256.

## Test

`dotnet test Fire3D/Fire3D.AuthTests/Fire3D.AuthTests.csproj --filter FullyQualifiedName~PasswordReset`

Để chạy cả PostgreSQL tests, đặt `FIRE3D_RESET_TEST_ADMIN` trỏ tới server local
loopback dành riêng cho test, có quyền CREATEDB. Test tạo/xóa database tên ngẫu
nhiên; không dùng connection string ứng dụng. Không suy ra Firebase/Mailgun thật
hoạt động từ các provider mock tests. Trước deploy cần test email thật, code hết
hạn/dùng lại, password policy Firebase, phiên cũ bị 401 và đăng nhập lại.
