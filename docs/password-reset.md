# Email/password local và Google Firebase

Đây là ghi chú vận hành, đối chiếu source BE `e42a2eb` ngày 04/10/2026 với [Docs v7](../../Docs/schema_v7_contract.md). Source có Forgot/Reset và Change Password handlers/worker; provider thật cần cấu hình/kiểm chứng riêng. Registration/Google flow đầy đủ tại [authentication](authentication.md).

BE quản lý mật khẩu thường bằng ASP.NET Core PasswordHasher: hash có salt nằm ở
`users.password_hash`, property C# `PasswordHash`. Không trả hash qua API. Supabase
chỉ cung cấp PostgreSQL; không dùng Supabase Auth. Firebase chỉ xác minh Google.

## Contract hiện tại

| API | Body | Thành công | Bearer |
|---|---|---|---|
| POST /api/auth/register | Form Trainee gồm username/confirmPassword/registrationToken sau OTP; xem authentication | 201, AccountResponse | Không |
| POST /api/auth/login | email, password | 200, accessToken/refreshToken/user | Không |
| POST /api/auth/login-firebase | JSON string chứa Firebase ID token | 200, TokenResponse | Không |
| POST /api/auth/forgot-password | email | 202, message chung | Không |
| POST /api/auth/reset-password | token, newPassword | 204 | Không |
| POST /api/auth/refresh | refreshToken | 200, TokenResponse | Không |
| GET /api/auth/me | — | 200, AccountResponse | Có |

Register là alias Trainee; route OrganizationUser riêng nhận hồ sơ tổ chức. Cả hai consume one-time proof sau OTP trước khi tạo identity; client không gửi role/tenant tùy ý. Email được trim/lowercase.
Password mới 6–128 ký tự; fullName 1–200 ký tự. Sai mật khẩu/email không tồn tại
cùng trả 401, tài khoản/organization bị khóa trả 403 sau khi xác minh mật khẩu.
Refresh là anonymous theo JWT nhưng yêu cầu refreshToken hợp lệ trong body; Bearer sai không biến route thành bắt buộc đăng nhập. Route có giới hạn 10 request/IP/phút, fixed window riêng mỗi instance, không Redis. Quota bao gồm request token sai: trong quota trả401, vượt quota trả429 `AUTH_REFRESH_RATE_LIMITED`, `traceId` và `Retry-After`. Middleware quyết định connection IP; không tin trực tiếp header IP từ client. CORS chạy trước rate limiter; origin được cho phép đọc response429 và header `Retry-After` đã expose. Preflight không tiêu thụ quota refresh. Các route anonymous khác giữ policy hiện hành của từng luồng.

Google yêu cầu Firebase token xác minh được, chưa revoke, email_verified=true và
sign_in_provider=google.com. UID đã liên kết giữ role/organization trong DB. Email
trùng tài khoản khác trả 409 ACCOUNT_LINK_REQUIRED, không tự gắn UID. Chưa có API
explicit Google linking/onboarding completion. UID mới chỉ nhận OnboardingRequired, chưa tạo user; account Google sau khi hoàn tất onboarding sẽ có password_hash nullable theo thiết kế đích.

## Reset mật khẩu và tài khoản Firebase cũ

Docs technology mục 14.8 còn một câu trạng thái cũ nói reset handler chưa hoàn tất revoke family. Câu đó không còn đúng với source BE ở commit nêu trên: `LocalPasswordReset` khóa user, consume token, revoke session và commit cùng transaction. Giữ contract đó làm tiêu chí; xác nhận lại theo source khi commit thay đổi.

Worker đọc queue bền vững `password_reset_email_jobs`, tạo token ngẫu nhiên 32 byte,
lưu SHA-256 của token ở `local_password_reset_tokens` rồi gửi Mailgun. Link:
`<AuthEmail:FrontendUrl>/reset-password?token=<64 ký tự hex>`.

Frontend đọc `token`, gọi BE reset-password với `token` và `newPassword`. Không gọi
Firebase confirm, không gửi `oobCode`. Link Firebase cũ không dùng được với API local.
Token sống 30 phút, dùng một lần; reset thành công vô hiệu mọi token reset còn lại
của user và thu hồi mọi refresh-token family, khiến access JWT cũ bị từ chối khi
kiểm tra phiên. Hash mới, consume token, revoke và audit cùng một transaction dưới
user advisory lock. Login xác minh hash dưới cùng lock nên không cấp phiên bằng
mật khẩu cũ sau khi reset đã commit. Audit/DB failure rollback toàn bộ.

Forgot trả 202 chung kể cả email không tồn tại. Tài khoản Firebase cũ không có hash
không thể đăng nhập local bằng mật khẩu Firebase cũ: chủ email dùng reset để đặt
mật khẩu BE mới. Việc này không thay đổi mật khẩu Firebase. Chủ email của tài khoản
Google cũng có thể thiết lập mật khẩu local qua cùng quy trình, giữ nguyên UID.

Queue có cooldown một phút/email, lease hai phút, timeout việc 45 giây, tối đa năm
lần thử và backoff. Email có thể lặp nếu Mailgun đã nhận nhưng worker chết trước ack.
Token của các lần gửi chưa hết hạn cùng dùng được cho tới khi một lần reset thành công.
Theo dõi job Dead và dọn dữ liệu token hết hạn/job cũ theo chính sách lưu giữ.

## Migration và cấu hình

Áp dụng `database/002_password_reset_recovery.sql` một lần nếu chưa có hai bảng đó,
sau đó `database/003_local_password.sql`. Script 003 không gán mật khẩu mặc định,
không ghi đè dữ liệu, giữ firebase_uid nullable và khóa truy cập bảng reset từ
anon/authenticated; cấp role backend fire3d_api/fet3d_backend_executor phù hợp.
Hai bảng của 002 vẫn phục vụ queue và bảo vệ phiên với operation Firebase cũ; không
xóa Pending cũ nếu chưa đối soát. Luồng local mới không tạo operation Firebase.
Các script SQL này cần chạy riêng, không được thay bằng EF EnsureCreated.

- `ConnectionStrings__DefaultConnection`: PostgreSQL backend connection.
- `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey`: JWT Fire3D; signing key base64 đủ độ dài.
- `AuthEmail__FrontendUrl`: HTTPS base URL của FET3D (ví dụ `https://fet3d.io.vn`), không query/fragment. HTTP loopback chỉ hợp lệ ở Development; Production từ chối cấu hình loopback khi khởi động.
- `AuthEmail__VerificationUrl`: URL public phục vụ `/verify-email/`. Với deployment FET3D hiện tại đặt `https://fet3d.io.vn`; nếu bỏ trống, hệ thống dùng `FrontendUrl` để tương thích cấu hình cũ.
- `AuthEmail__WorkerEnabled`: true để gửi email, false khi test không cần worker.
- `AuthTokenCleanup__Enabled`: bật/tắt worker dọn refresh token; mặc định `true`.
- `AuthTokenCleanup__IntervalMinutes`, `AuthTokenCleanup__RetentionDays`, `AuthTokenCleanup__BatchSize`, `AuthTokenCleanup__MaxBatchesPerRun`: mặc định là `60`, `7`, `500`, `10`. Worker chỉ xóa family khi mọi refresh token trong family đã hết hạn ít nhất `RetentionDays`; access JWT không lưu trong database.
- Cleanup chọn batch theo hạn cuối family, user ID rồi family ID để thứ tự ổn định trên PostgreSQL. Sau khi lấy khóa user, nó kiểm tra lại mọi token trong family trước khi xóa; token mới hoặc thành viên chưa qua retention giữ toàn bộ family. Không cần migration cho sửa query này.
- PostgreSQL regression kiểm generation email bằng kiểu `integer` thực tế. Resend legacy cùng account chạy cạnh tranh chỉ tạo một generation sau cooldown và worker của generation cũ bị chặn; link legacy đã phát hành giữ contract riêng. Luồng OTP mới vô hiệu mã/proof cũ khi resend được chấp nhận, và proof đăng ký chỉ dùng một lần.
- `Mailgun__ApiKey`, `Mailgun__Domain`, `Mailgun__From`, `Mailgun__BaseUrl`: Mailgun.
- Firebase Admin credential: chỉ cần cho Google; secret JSON, section hoặc file local.
  Email/password/reset local không cần Firebase Web API key hoặc lời gọi Firebase.

Không commit credential, password, raw reset token hoặc link reset. Không đưa chúng
vào log/analytics. Các lớp reset Firebase cũ còn trong repo cho tương thích/lịch sử,
nhưng controller reset và worker hiện dùng ILocalPasswordReset.

## Kiểm tra

`dotnet test Fire3D/Fire3D.AuthTests/Fire3D.AuthTests.csproj --filter "FullyQualifiedName~LocalAuthentication|FullyQualifiedName~AnonymousAuthHttp|FullyQualifiedName~PasswordReset"`

Đặt FIRE3D_RESET_TEST_ADMIN tới PostgreSQL test loopback có quyền CREATEDB để chạy
integration SQL. Tests tạo/xóa DB riêng, không dùng Supabase. HTTP anonymous tests
dùng middleware thật với handler giả; PostgreSQL flow test dùng handler/store thật.
Chưa xác nhận Google/Mailgun thật bằng kết quả mock tests.

Checklist triển khai Mailgun/Azure và mẫu biến môi trường không có secret: [azure-mailgun-deployment.md](azure-mailgun-deployment.md).
