# Xác thực và tài khoản BE

## Chuẩn sản phẩm

Đọc [FR-AUTH](../../Docs/fire_evacuation_requirements.md), [workflow đăng ký/profile](../../Docs/fire-evacuation-training-workflows.md) và [technology contract](../../Docs/fire-evacuation-training-technology.md). Chuẩn hiện hành là:

Đối chiếu ngày 04/10/2026 tại BE `e42a2eb`: [v7 contract](../../Docs/schema_v7_contract.md) biểu diễn OTP challenge/proof, reset email jobs và installation bền vững. Tên bảng đích không chứng minh EF mapping/grants đã khớp; xem DB-01 trong checklist.

- BE quản lý email/password local, password hash và Fire3D session. Supabase chỉ là PostgreSQL; không dùng Supabase Auth.
- Firebase xác minh Google identity. Firebase không cấp role nghiệp vụ. Role, trạng thái và tenant lấy từ PostgreSQL.
- Trainee đăng ký local với email, username, password và confirm password. OrganizationUser đăng ký với email/password và hồ sơ organization; username cá nhân tùy chọn. Client không tự chọn role/tenant.
- Google identity mới phải qua onboarding ngắn hạn và chọn Trainee hoặc OrganizationUser. Identity đã liên kết đăng nhập theo role/tenant hiện có. Không tự nối tài khoản chỉ vì email trùng.
- Trainee username lowercase, duy nhất toàn hệ thống và hoàn tất lúc đăng ký/onboarding; không yêu cầu username ở game start.
- Forgot/Reset Password là một luồng: email link một lần đặt mật khẩu mới. Change Password là luồng trong phiên đăng nhập, xác minh mật khẩu hiện tại rồi nhận mật khẩu mới. Cả hai thu hồi các refresh session theo contract; chỉ reset gửi Mailgun.
- Google UID nullable/unique khi liên kết. Link Google vào tài khoản local cần chứng minh đã xác thực tài khoản local; không tự động link.

## Hiện trạng source BE trên nhánh triển khai (không suy ra deployment/main)

Cập nhật luồng ngày **03/10/2026**, đối chiếu controller, handler và store trong checkout. Bằng chứng HTTP Azure và phần provider chưa nghiệm thu nằm tại [payos-deployment.md](payos-deployment.md); tài liệu flow không chứng minh email/Google production đã hoạt động.

### Luồng form trước, OTP trước khi tạo identity

FE nhập form và giữ trong bộ nhớ, chuyển sang màn hình OTP. `request-otp` nhận email; `resend-verification` gửi lại OTP sau cooldown. Email đã có account trả **409 EMAIL_EXISTS + errors.email**, kể cả khác casing hoặc account bị khóa/xóa mềm; không enqueue email. Email mới trả 202, chưa có identity. Đây là quyết định thông báo email trùng cho form; forgot-password vẫn che account existence bằng 202.

Nút “Xác thực và đăng ký” gọi verify-otp rồi, khi nhận proof, gửi toàn bộ JSON form + registrationToken tới register/trainee hoặc register/organization. Verify không kiểm mật khẩu và chưa tạo account. Validation cuối cùng chạy ở register; consume proof, identity/organization và audit atomic. Form sai không làm mất proof; proof đã dùng không tạo account thứ hai. 201 trả account, phải login để lấy JWT.

Password chỉ giữ trong bộ nhớ FE. Đổi email bỏ proof và xác minh lại; reload mất form quay lại form. Không lưu password vào URL hay web storage. `/check-email/` chỉ là demo OTP/proof, không đại diện cho form FE hoàn chỉnh. [Kịch bản Swagger và JSON đầy đủ](registration-payos-manual-test.md).

Migration additive `20261003030000_AddNormalizedRegistrationEmail` thêm unique expression index `lower(btrim(email))`, giữ index/cột/data cũ. Nếu legacy có duplicate chuẩn hóa, migration dừng để review; không tự merge/xóa account. Chỉ mapping các constraint email/username/slug đã biết; vi phạm unique không liên quan không giả thành EMAIL_EXISTS.

```mermaid
sequenceDiagram
    participant FE
    participant BE
    participant DB as PostgreSQL
    participant Worker as Email worker
    FE->>FE: Nhập form Trainee hoặc OrganizationUser, giữ trong bộ nhớ
    FE->>BE: request-otp(email)
    BE->>DB: Kiểm email trùng, cooldown/quota; lưu challenge và job
    BE-->>FE: 202 nếu email mới; 409 nếu email đã có
    Worker->>DB: Claim challenge còn hiệu lực
    Worker->>Worker: Gửi OTP qua Mailgun
    FE->>BE: verify-otp(email, otp)
    BE-->>FE: registrationToken và expiresAt, chưa tạo account
    FE->>BE: register/trainee hoặc register/organization (toàn bộ form + proof)
    BE->>DB: Validate; consume proof, tạo identity/organization và audit atomic
    BE-->>FE: 201 AccountResponse
    FE->>BE: login(email, password)
    BE-->>FE: 200 accessToken, refreshToken, user
```

Quy tắc FE/BE cho cả hai loại account:

- `request-otp` và `resend-verification` cùng gọi `RequestRegistrationOtpCommand`/`RegistrationOtpStore`. Trong 60 giây trả202 nhưng không tạo job mới; sau cooldown tạo mã mới, vô hiệu challenge/proof cũ. Quota 5/email/giờ, 20/IP/giờ; vượt quota429 OTP_RATE_LIMITED cùng Retry-After. 202 không chứng minh email đã delivered.
- OTP là chuỗi sáu số, giữ số 0 đầu, hạn 10 phút. Verify sai đủ 5 lần vô hiệu challenge. Proof hạn 15 phút, gắn email và dùng một lần trong transaction register; proof không phải access token.
- Trainee gửi email/username/password/confirmPassword; username chuẩn hóa lowercase, 3–30 ký tự `a-z`, số, `.`, `_`, `-`. OrganizationUser gửi email/password/confirmPassword và organizationName/address/phone; route hiện tại không nhận username cá nhân. Các field fullName/dob/gender/phoneNumber theo DTO từng route.
- Password mới 6–128 ký tự, không chỉ khoảng trắng, không tự trim; confirmPassword khớp chính xác. DOB `YYYY-MM-DD`, không tương lai; phone chuẩn hóa 6–15 chữ số, có thể có dấu`+` đầu. JSON enum gửi tên.
- Register thiếu/sai/hết hạn/đã dùng proof trả400 EMAIL_VERIFICATION_REQUIRED; email/username trùng409. Validation form sai chưa consume proof. Organization và owner được tạo hoặc rollback cùng nhau; client không chọn role, organizationId hoặc gia nhập tenant có sẵn.
- Account mới có emailVerifiedAt dạng timestamp và registrationExpiresAt=null. Registration trả account, không cấp JWT; cần login riêng. Không cần job xóa user chưa verify cho luồng OTP mới vì trước register chưa có user. `/verify-email` chỉ hỗ trợ link pending legacy; forgot-password là luồng reset riêng.

Nguồn: [AuthController](../Fire3D/Fire3D.API/Controllers/AuthController.cs), [SelfRegistrationCommands](../Fire3D/Fire3D.Application/Authentication/Commands/SelfRegistration/SelfRegistrationCommands.cs), [RegistrationOtpStore](../Fire3D/Fire3D.Infrastructure/Authentication/RegistrationOtpStore.cs), [RegistrationOtpEmailWorker](../Fire3D/Fire3D.Infrastructure/Workers/RegistrationOtpEmailWorker.cs).

### Luồng đăng nhập, phiên và đăng xuất

1. FE gọi `POST /api/auth/login` bằng email/password. BE chuẩn hóa email, tìm account, khóa user và đọc lại; kiểm password hash, account/organization đang hoạt động và điều kiện pending legacy. Login không áp minimum6 của password mới, chỉ yêu cầu không rỗng/tối đa128 để tương thích tài khoản cũ.
2. Password sai/email không có trả401 INVALID_CREDENTIALS; account/organization bị khóa 403 ACCOUNT_DISABLED. Account pending legacy chưa xác thực/hết hạn trả403 EMAIL_NOT_VERIFIED/REGISTRATION_EXPIRED. Account OTP mới đã verified tại register.
3. Login thành công tạo session family mới, lưu hash refresh token, cập nhật lastLoginAt và audit cùng transaction. Response200 gồm accessToken/refreshToken/user; LoginResponse và TokenResponse không có field expiresAt. TTL do cấu hình Jwt quyết định; mặc định code15 phút access và7 ngày refresh.
4. FE gửi Bearer vào API bảo vệ. Middleware kiểm chữ ký/issuer/audience/expiry rồi đọc DB để kiểm account, organization, role/tenant và family. Access JWT chưa tới exp vẫn bị từ chối sau khi family bị revoke.
5. `POST /api/auth/refresh` nhận refreshToken mới nhất, không cần Bearer. Rotation consume token cũ, cấp cặp mới trong cùng family, giữ hạn tuyệt đối ban đầu. Replay token đã consume/revoke thu hồi family và trả401 INVALID_REFRESH_TOKEN. FE cần đồng bộ refresh, thay cả hai token sau200; policy 10/IP/phút trên mỗi instance trả 429 AUTH_REFRESH_RATE_LIMITED/Retry-After khi vượt quota.
6. `POST /api/auth/logout` dùng Bearer, không body, revoke family hiện tại và trả204. `POST /api/auth/logout-all` kiểm lại family dưới khóa, revoke tất cả family, tắt push bindings và ghi audit atomic, trả204. FE xóa token local; access/refresh cũ bị từ chối. Login mới sau commit tạo family mới; đăng ký device lại nếu cần nhận push.

`GET /api/auth/me` đọc hồ sơ và ETag; mutation profile/Avatar yêu cầu If-Match. Đăng ký device cần UUID installation và X-Installation-Key ngoài Bearer, không đồng nghĩa với việc login cấp phiên thành công.

Nguồn: [LoginWithPasswordCommand](../Fire3D/Fire3D.Application/Authentication/Commands/LoginWithPassword/LoginWithPasswordCommand.cs), [RefreshTokenCommandHandler](../Fire3D/Fire3D.Application/Authentication/Commands/RefreshToken/RefreshTokenCommandHandler.cs), [ValidateSessionQueryHandler](../Fire3D/Fire3D.Application/Authentication/Queries/ValidateSession/ValidateSessionQueryHandler.cs), [LogoutAllCommand](../Fire3D/Fire3D.Application/Authentication/Commands/Logout/LogoutAllCommand.cs), [AuthenticationExtensions](../Fire3D/Fire3D.API/Extensions/AuthenticationExtensions.cs).

### Google: nhánh đang có và phần còn thiếu

FE gửi Firebase ID token dưới dạng JSON string tới `POST /api/auth/login-firebase`. UID đã link trả200 `{status:"Authenticated", authentication:{accessToken,refreshToken,user}}`; email local trùng nhưng UID chưa link trả409 ACCOUNT_LINK_REQUIRED; UID/email mới trả200 `{status:"OnboardingRequired"}`. Không tự tạo account hoặc link chỉ từ email trùng. Chưa có endpoint/proof onboarding completion hoặc explicit Google link để hoàn tất hai nhánh còn thiếu; không hướng dẫn FE coi OnboardingRequired là đã đăng nhập.

Nguồn: [ExchangeFirebaseTokenCommand](../Fire3D/Fire3D.Application/Authentication/Commands/FirebaseLogin/ExchangeFirebaseTokenCommand.cs).

| Năng lực | Source hiện có | Khác biệt so với contract đích |
|---|---|---|
| Local registration | Hai route Trainee/OrganizationUser dùng cùng flow form → OTP → proof → register → login; alias `/register` dùng contract Trainee. | FE form chưa kiểm chứng tích hợp; Google onboarding/link còn thiếu |
| Email verification | OTP registration worker có challenge 10 phút, resend 1 phút, 5 email/giờ, 20 IP/giờ và proof 15 phút dùng một lần. `POST /api/auth/registration/request-otp` gửi OTP lần đầu; `POST /api/auth/resend-verification` gửi OTP mới và vô hiệu mã/proof cũ; `/check-email/` dùng hai route này. `/verify-email` vẫn deprecated cho link của account pending legacy. | Provider Mailgun production vẫn cần nghiệm thu riêng. |
| Local login/session | Login, refresh rotation/replay revoke, logout, logout-all và `/api/auth/me` dùng session family trong DB | Provider/deployment và client cần nghiệm thu riêng; không xóa family để bỏ validation |
| Google | `POST /api/auth/login-firebase`; UID đã liên kết trả `Authenticated`, email local trùng trả `ACCOUNT_LINK_REQUIRED`, Google mới trả `OnboardingRequired` và không tự tạo tài khoản | Chưa có endpoint onboarding/link tường minh để hoàn tất chọn loại tài khoản |
| Profile | GET/PATCH cá nhân có ETag; PATCH sửa fullName/username/dob/gender/phoneNumber. GET/PATCH organization đã có cho OrganizationUser, ETag riêng. Avatar có decoder, candidate trước copy và cleanup/recovery worker. | Không coi schema Swagger trống là route thiếu; còn cần kiểm chứng provider/deployment và recovery đúng môi trường |
| Reset password | Forgot tạo job bền vững; worker gửi Mailgun; reset tiêu thụ token, đổi hash, thu hồi phiên và ghi audit trong user transaction | Core local reset đã có code; cần đối chiếu bảng token với schema target và kiểm thử rollback/race. Chưa khẳng định Mailgun production đã gửi thật |
| Change password | `POST /api/auth/change-password` kiểm tra mật khẩu hiện tại, lưu mật khẩu mới, vô hiệu token reset và thu hồi phiên trong user transaction | Đã có code; không gửi email; cần giữ kiểm tra rollback/session cũ khi đồng bộ schema |

Chi tiết request/response hiện tại nằm ở [API guide](api-docs.md). Chi tiết worker/token/cấu hình nằm ở [password-reset.md](password-reset.md). Không copy contract endpoint mục tiêu thành route “đang có” nếu controller chưa triển khai.

## Hướng triển khai khi sửa auth

1. Hoàn tất Google onboarding idempotent, giữ role/tenant của UID đã link, yêu cầu xác thực local trước khi link vào email local hiện có.
2. Kiểm chứng organization profile/Avatar recovery và sửa OpenAPI đang thiếu schema PATCH, multipart/header; các route, decoder và cleanup đã có source. Không nhận URL/avatar data tùy ý từ client.
3. Giữ reset và change thành hai use case riêng nhưng dùng cùng chính sách transaction, token/session revoke và audit của Docs. Core transaction hiện có; đối chiếu mapping schema token và kiểm thử PostgreSQL trước khi coi luồng hoàn tất. Không dùng Firebase password reset cho local password.

## Quy tắc bảo mật

Registration and profile validation return ProblemDetails 400 with a stable `code` of `VALIDATION_ERROR`, an `errors` map keyed by request field, and a `traceId`. Passwords, tokens, credentials and raw provider errors are never included in validation responses or logs.

- Không lưu/trả password plaintext, password hash, reset token thô, Firebase Admin credential, access/refresh token trong log.
- Không nhận role, organizationId, trạng thái hoặc actor tin cậy từ request của client.
- Tài khoản, organization và session phải được xác minh ở BE; mọi truy vấn nghiệp vụ áp dụng role và tenant scope ở server.
- Khi login/password reset chạy đồng thời, bảo đảm khóa user/transaction tránh cấp phiên từ mật khẩu cũ sau reset commit.
- Không tuyên bố xác minh Google, gửi Mailgun, revoke token, migration hoặc quyền DB production đạt nếu chỉ mới có unit/mock test.

## Kiểm tra

Chạy auth test phù hợp theo [integration-tests.md](integration-tests.md). Bao gồm ít nhất: đăng ký role đúng và email/username collision; Google UID mới/đã liên kết/trùng email/race; onboarding retry; profile ETag; reset token hết hạn/dùng lại; change sai mật khẩu cũ; revoke session và rollback khi audit/DB lỗi. Tách test local PostgreSQL khỏi provider test thật.
