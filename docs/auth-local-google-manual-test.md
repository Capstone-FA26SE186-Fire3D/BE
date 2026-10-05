# Test tay local auth và Google exchange

Phạm vi: hai task password recovery/session và Google exchange. Không nghiệm thu onboarding/link, S3, email hoặc Firebase production bằng kết quả mock.

## Chuẩn bị

- Chạy đúng nhánh `fix/auth-login-performance`; migrations/permissions phải có trong database test riêng. Migration mới `20261005090000_AddPasswordRecoveryGate` chưa áp Supabase trong đợt này. API runtime dùng login giới hạn quyền, migration dùng identity triển khai; không cấp DELETE bảng legacy cho API.
- Dùng tài khoản local test đã verified, đang active; OrganizationUser cần tổ chức active. Giữ mật khẩu/token trong công cụ test, không đưa vào Git/log.
- Swagger dùng cùng HTTPS origin. Email thật chỉ test khi đã cấu hình provider và được phép gửi tới hộp thư test; automated test dùng provider giả.

## Login và change-password

1. `POST /api/auth/login`, body `{"email":" test@example.com ","password":"<password hiện tại>"}`. Email uppercase/space cũng tìm đúng account. Kỳ vọng200 với accessToken/refreshToken/user.
2. Login lần nữa để có family thứ hai. Authorize bằng accessToken của lần đầu.
3. `POST /api/auth/change-password`, body `{"currentPassword":"<password hiện tại>","newPassword":"<password mới 6–128 ký tự>"}`. Không gửi userId/familyId. Kỳ vọng204; hai access/refresh token cũ không còn dùng được.
4. Sai currentPassword trả400 INVALID_CURRENT_PASSWORD; password không đổi trả400 PASSWORD_UNCHANGED. Login với password cũ401, mới200.
5. Sau logout/logout-all, dùng access token cũ change-password trả401. Race request đã qua middleware nhưng family bị revoke được bao phủ bằng PostgreSQL test; Swagger tuần tự không chứng minh race.

## Forgot/reset

1. `POST /api/auth/forgot-password`, body `{"email":"test@example.com"}`. Kỳ vọng202 chung cho email tồn tại/không tồn tại, Google-only hoặc lifecycle không đủ điều kiện. Không suy ra email đã delivered từ202.
2. Với local account hợp lệ và mail worker/provider test đã bật, lấy **reset token** từ email mới; không dùng OTP đăng ký hoặc token legacy GUID.
3. `POST /api/auth/reset-password`, body `{"token":"<64 ký tự hex từ reset email>","newPassword":"<password mới>"}`. Kỳ vọng204; session và reset token khác của cùng user mất hiệu lực.
4. Gọi lại token vừa dùng hoặc token cũ khác:400 INVALID_RESET_CODE. Token hết hạn/Google-only/account hoặc organization bị khóa không đổi password. Account khác không bị revoke.
5. Trong DB test riêng: reset rows vẫn tồn tại, used_at được đánh dấu; audit và password/session/token rollback cùng nhau nếu audit lỗi. Không inject lỗi trên Supabase dùng chung.

## Google qua Firebase

`POST /api/auth/login-firebase` nhận **JSON string**:

```json
"<Firebase ID token từ Google sign-in>"
```

| Trường hợp | Kỳ vọng |
|---|---|
| Google UID đã liên kết account active | 200 Authenticated, token Fire3D và role/tenant từ DB |
| UID/email mới | 200 OnboardingRequired; chưa tạo account/organization và chưa có JWT |
| Email local có sẵn nhưng UID chưa link | 409 ACCOUNT_LINK_REQUIRED; không tự link |
| Token sai/expired/revoked; provider password/anonymous; email chưa verified | 401 INVALID_FIREBASE_TOKEN |
| Account/organization bị khóa dưới khóa | 403 ACCOUNT_DISABLED |
| UID đổi chủ lúc chờ khóa | 409 ACCOUNT_CHANGED, không cấp session cho owner mới |
| Provider mạng/cert fetch/timeout15 giây | 503 GOOGLE_PROVIDER_UNAVAILABLE |

Chỉ dùng Firebase token cùng project deploy, không dùng Google access token hoặc JWT Fire3D làm input. Revocation/timeout/UID race được kiểm qua SDK giả lập và mock; kiểm provider thật cần môi trường Firebase test riêng. Client hủy request giữ cancellation, không đổi thành lỗi token. Không log claims/token. Onboarding completion/link explicit vẫn chưa triển khai.

## Bằng chứng tự động

- `LocalAuthRecoveryPostgresTests`: restricted runtime role, legacy row preservation, reset replay/concurrency, revoked-family race, organization lifecycle và audit rollback trên PostgreSQL disposable.
- `PasswordResetPostgresTests` dùng fixture schema tối thiểu và gate thật; không chứng minh toàn bộ schema target/production.
- `FirebaseGoogleVerificationTests`: actual SDK error mapping với verifier giả, Google/email verified, checkRevoked, deadline/cancellation.
- `GoogleExchangeHardeningTests` và `GoogleExchangeHttpTests`: ownership/lifecycle, các nhánh response và HTTP401/503 có code/traceId. Không gọi provider thật.
