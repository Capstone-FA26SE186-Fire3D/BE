# Fire3D — Hướng dẫn tích hợp API hiện tại

Cập nhật contract **04/10/2026** theo [Docs v7](../../Docs/schema_v7_contract.md) và rà source BE `e42a2eb`. Các mục endpoint/editor bắt đầu từ baseline `946017d` ngày 23/09, có bổ sung auth/billing tháng 10; số endpoint ở heading là danh mục lịch sử, cần kiểm controller khi tích hợp. Đây là hướng dẫn API source hiện có, không chứng minh toàn bộ capability v7 đã hoàn thành.

Các phần dưới có baseline lịch sử riêng; không dùng số endpoint cũ để suy mức hoàn thiện hiện tại. Bổ sung **02/10/2026**: catalog, quotation Building và enterprise contact request tại [billing.md](billing.md), gồm route/quyền, If-Match, Idempotency-Key, ví dụ và luồng checkout PayOS, webhook, entitlement/reconcile đã có code/test. Cập nhật **03/10/2026**: migration PayOS/email và login giới hạn quyền đã áp vào Supabase; API local tạo link/QR provider thật thành công, chưa chuyển tiền. Azure đã phục vụ Swagger/OpenAPI/return/cancel và CORS FE, nhưng worker/executor/webhook/Paid/provisioning deployment vẫn chưa nghiệm thu. Đối chiếu OpenAPI/source và [implementation checklist](api-implementation-checklist.md) khi tích hợp; endpoint tồn tại không chứng minh provider đã hoạt động.

### Giới hạn OpenAPI deploy đã kiểm tra ngày 03/10/2026

OpenAPI tại host Azure tự báo build `d785937e5948f31486df66740a1f31ccd9ce25a5`, có 118 operation HTTP GET/POST/PUT/PATCH/DELETE; đây là số metadata, không phải số capability đã hoàn tất. Server `/` dùng cùng HTTPS origin.

- Avatar upload đang mô tả `application/x-www-form-urlencoded` với thuộc tính IFormFile. Contract runtime cần `multipart/form-data`, field `file` và `If-Match`; Swagger chưa thể hiện nút file đúng.
- `UpdateCurrentProfileRequest` và `UpdateOrganizationProfileRequest` đang là schema `{}`; không hiểu là endpoint không có field.
- Schema enum UserGender/UserRole đang mô tả integer trong khi JSON runtime cấu hình enum theo tên; gửi tên enum theo contract.
- `registrationToken` của register và `If-Match` của Avatar upload chưa đánh dấu required; BE vẫn yêu cầu chúng. GET trạng thái PayOS chưa mô tả response thành công đầy đủ.

Đây là lỗi tài liệu OpenAPI còn cần sửa code generator/metadata; đợt cập nhật tài liệu này chưa sửa runtime. [Kết quả Azure và cấu hình CORS](payos-deployment.md) phân biệt kiểm tra HTTP với nghiệm thu provider thật.

## 1. Quy ước tích hợp

- Dùng origin BE đang chạy làm `BASE_URL`; đường dẫn bên dưới đã có `/api`.
- Swagger `/swagger`, OpenAPI `/openapi/v1.json` và health `/health` không tính vào số action controller. Health không chứng minh DB/Mailgun/Firebase/storage đã kết nối thành công.
- Body: `Content-Type: application/json`, tên thuộc tính `camelCase`, GUID là chuỗi UUID, ngày giờ ISO 8601.
- Enum JSON dùng tên như `"OrganizationUser"`; không gửi số cho role.
- Không có envelope chung `{success,data}`. Đọc trực tiếp DTO. `204` và một số `200` không có body; không luôn gọi `response.json()`.
- POST tạo resource đồng bộ trả `201 Created` cùng header `Location`. POST yêu cầu xử lý nền bền vững trả `202 Accepted`; POST action trên resource đã có có thể trả `200` hoặc `204`.
- Các ID, token, hash, password minh họa phải thay bằng dữ liệu test thực tế.

### POST status và Location

| Path | Thành công | Header `Location` |
| --- | --- | --- |
| `POST /api/auth/register` | 201 AccountResponse | `/api/accounts/{id}` |
| `POST /api/auth/register/trainee` | 201 AccountResponse | `/api/accounts/{id}` |
| `POST /api/auth/register/organization` | 201 AccountResponse | `/api/accounts/{id}` |
| `POST /api/auth/registration/request-otp` | 202 Accepted / 409 EMAIL_EXISTS | Không tạo account; email đã có trả errors.email và không enqueue |
| `POST /api/auth/registration/verify-otp` | 200 RegistrationOtpVerificationResponse | Trả registrationToken 15 phút, dùng một lần |
| `POST /api/auth/resend-verification` | 202 Accepted | Gửi lại OTP đăng ký mới sau cooldown; OTP/proof trước đó bị vô hiệu |
| `POST /api/auth/verify-email` | 204 No Content | **Deprecated**. Chỉ xác minh token link legacy, single-use và hết hạn sau 15 phút |
| `POST /api/accounts` | 201 AccountResponse | `/api/accounts/{id}` |
| `POST /api/organizations` | 201 OrganizationResponse | `/api/organizations/{id}` |
| `POST /api/buildings` | 201 BuildingResponse | `/api/buildings/{id}` |
| `POST /api/buildings/{buildingId}/ifc` | 201 InitiateIfcUploadResponse | `/api/revisions/{revisionId}` |
| `POST /api/revisions/{revisionId}/process` | 202 | `/api/processing-jobs/{jobId}` |
| `POST /api/processing-jobs/{jobId}/retry` | 202 | `/api/processing-jobs/{jobId}` |
| `POST /api/auth/forgot-password` | 202 | Không có |

`login`, `login-firebase`, `refresh`, validate draft, publish, confirm-for-training và start playtest trả 200 vì không tạo API resource mới. `logout`, `reset-password` và `upload-complete` trả 204 vì không có body thành công.

### Bearer, role và phạm vi tổ chức

Endpoint bảo vệ cần `Authorization: Bearer <Fire3D accessToken>`. Firebase ID token chỉ là body của login-firebase, không thay JWT Fire3D.

| Ký hiệu | Điều kiện |
| --- | --- |
| Public | Không cần Bearer |
| User | Phiên Fire3D hợp lệ |
| Admin | PlatformAdmin; handler kiểm tra lại tài khoản |
| Editor | OrganizationUser trong tổ chức mình hoặc PlatformAdmin qua `IfcAccess`; Trainee bị 403 |
| Building CRUD | Controller chỉ có Authorize, store lọc organization_id của JWT; có giới hạn bên dưới |

Ba role hiện có: `PlatformAdmin`, `OrganizationUser`, `Trainee`. Không có `OrganizationAdmin`. OrganizationUser phải có tổ chức hoạt động; hai role còn lại không có organizationId. Public register không cho chọn role/tổ chức. JWT được kiểm tra cả tài khoản, tổ chức và phiên DB; chưa tới exp vẫn có thể mất hiệu lực khi phiên bị thu hồi.

**Building CRUD:** create/update/archive kiểm actor/role/tenant từ DB và mutation/audit trong cùng transaction, khóa lifecycle/user. PlatformAdmin cần query `organizationId` đích cho mutation; OrganizationUser dùng tenant mình, không được đổi tenant. Body hiện chưa nhận tenant đích. Thiếu scope bị handler từ chối, không coi Guid.Empty là quyền admin. List/detail vẫn lọc organization claim (admin không có tenant có thể nhận list rỗng/404); không suy từ quyền mutation rằng read CRUD đã hỗ trợ admin toàn nền tảng.

### Lỗi và phân trang

Lỗi nghiệp vụ thường dùng ProblemDetails:

```json
{ "title": "Invalid email or password.", "status": 401, "code": "INVALID_CREDENTIALS" }
```

Framework có thể thêm type/traceId/errors. Building CRUD, một số IFC command và publish không thêm code. Middleware 401/403/429 có thể không có JSON. Ưu tiên HTTP status rồi mới đọc body nếu có.

| Status | Ý nghĩa |
| --- | --- |
| 400 | Input/filter hoặc trạng thái tài nguyên không hợp lệ |
| 401 | Thiếu/sai/hết hiệu lực token hoặc sai thông tin đăng nhập |
| 403 | Không đủ role; một số login báo tài khoản/tổ chức bị khóa |
| 404 | Không thấy, ngoài phạm vi tổ chức hoặc ID URL không đúng định dạng GUID |
| 409 | Trùng email/slug, xung đột version/trạng thái/idempotency |
| 412 | Thiếu/sai If-Match khi lưu draft |
| 422 | Không xác minh được object upload |
| 429 | Vượt rate limit |

Policy administration: **120 request/phút/IP** cho administration và IFC commands. `POST /api/auth/refresh` có policy riêng **10 request/phút/IP**, fixed window trong bộ nhớ mỗi instance, không Redis. Vượt quota trả `429 AUTH_REFRESH_RATE_LIMITED`, ProblemDetails có `traceId` và header `Retry-After` (giây). Trong quota, refresh token sai vẫn trả `401`; header IP client tự gửi không thay thế connection IP do middleware xử lý. CORS chạy trước rate limiter và expose `Retry-After` cho origin frontend được cho phép; preflight không tiêu thụ quota. Nhiều instance không chia sẻ quota này.

Phân trang mặc định page=1, pageSize=20; page 1..100000, pageSize 1..100. Search quản trị/building tối đa 200 ký tự. `Page<T>` bên dưới có dạng:

```json
{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }
```

## 2. Authentication

Luồng và bằng chứng source chi tiết tại [authentication.md](authentication.md): cả Trainee và OrganizationUser dùng form → OTP → proof → register → login; JWT chỉ được cấp tại login/refresh hoặc Google UID đã liên kết. Bảng dưới liệt kê API, không dùng số endpoint lịch sử để kết luận auth hoàn tất.

| Method | Path | Quyền | Thành công |
| --- | --- | --- | --- |
| POST | `/api/auth/register` | Public | 201 AccountResponse |
| POST | `/api/auth/register/trainee` | Public | 201 AccountResponse |
| POST | `/api/auth/register/organization` | Public | 201 AccountResponse |
| POST | `/api/auth/registration/request-otp` | Public | 202 Accepted |
| POST | `/api/auth/registration/verify-otp` | Public | 200 RegistrationOtpVerificationResponse |
| POST | `/api/auth/resend-verification` | Public | 202 Accepted |
| POST | `/api/auth/verify-email` | Public, deprecated | 204 No Content |
| POST | `/api/auth/login` | Public | 200 LoginResponse |
| POST | `/api/auth/login-firebase` | Public | 200 GoogleExchangeResponse; 401 invalid identity; 409 explicit link/race; 503 provider unavailable |
| POST | `/api/auth/google/onboarding/complete` | Public + onboarding proof | 201 AccountResponse; replay 200; 400 validation/expiry; 409 input/identity conflict |
| POST | `/api/auth/refresh` | Public | 200 TokenResponse |
| POST | `/api/auth/logout` | User | 204 |
| POST | `/api/auth/logout-all` | User | 204 |
| GET | `/api/auth/me` | User | 200 AccountResponse |
| PATCH | `/api/auth/me` | User | 200 AccountResponse |
| PUT | `/api/auth/devices` | User + `X-Installation-Key` | 200 DeviceRegistrationResponse |
| DELETE | `/api/auth/devices/{deviceUuid}` | User + `X-Installation-Key` | 204 |
| GET | `/api/organizations/me` | OrganizationUser | 200 OrganizationProfileResponse + ETag |
| PATCH | `/api/organizations/me` | OrganizationUser + `If-Match` | 200 OrganizationProfileResponse |
| POST/GET | `/api/feedback` | User | 201/200 |
| POST/GET | `/api/support/tickets` | User | 201/200 |
| GET | `/api/support/tickets/{id}` | Ticket owner | 200 |
| POST | `/api/support/tickets/{id}/messages` | Ticket owner | 201 |
| GET | `/api/admin/feedback` | PlatformAdmin | 200 |
| PATCH | `/api/admin/feedback/{id}/status` | PlatformAdmin | 200 |
| GET | `/api/admin/support/tickets` | PlatformAdmin | 200 |
| GET | `/api/admin/support/tickets/{id}` | PlatformAdmin | 200 |
| POST | `/api/admin/support/tickets/{id}/messages` | PlatformAdmin | 201 |
| PATCH | `/api/admin/support/tickets/{id}` | PlatformAdmin | 200 |
| GET | `/api/admin/audit-logs` | PlatformAdmin | 200 paged metadata |
| GET | `/api/admin/audit-logs/{id}` | PlatformAdmin | 200 metadata |
| GET | `/api/admin/analytics/operations` | PlatformAdmin | 200 aggregate snapshot |
| GET | `/api/organizations/me/analytics/operations` | OrganizationUser | 200 tenant aggregate snapshot |
| POST | `/api/auth/forgot-password` | Public | 202 với message chung |
| POST | `/api/auth/reset-password` | Public | 204 |
| POST | `/api/auth/change-password` | User | 204 |

### 2.1 Register local

```json
{
  "email": "trainee@example.com",
  "username": "nguyen.van.a",
  "password": "Example-Password-2026!",
  "confirmPassword": "Example-Password-2026!",
  "fullName": "Nguyen Van A",
  "dob": "2000-01-02",
  "gender": "PreferNotToSay",
  "phoneNumber": "+84123456789",
  "registrationToken": "<proof nhận từ verify-otp>"
}
```

Trước khi gọi một trong ba route register, client gọi `POST /api/auth/registration/request-otp` với `{ "email": "..." }` để nhận mã sáu chữ số từ email, rồi gọi `POST /api/auth/registration/verify-otp` với `{ "email": "...", "otp": "123456" }`. Kết quả thành công trả `{ "registrationToken": "...", "expiresAt": "..." }`; token hết hạn sau 15 phút, gắn với email và chỉ dùng một lần. Khi cần mã mới, client gọi `POST /api/auth/resend-verification`: sau 60 giây, request mới thay mã/proof cũ; mã có hạn 10 phút. Cả hai endpoint gửi OTP cùng có giới hạn 5 lần/email/giờ và 20 lần/IP/giờ; khi vượt giới hạn trả `429 OTP_RATE_LIMITED` cùng header `Retry-After`. Hai API này không tạo `users` hay `organizations`; job gửi OTP chạy tách khỏi worker link xác minh cũ.

`/api/auth/register/trainee` yêu cầu email hợp lệ tối đa 254 ký tự, username lowercase theo `[a-z0-9._-]{3,30}`, password 6–128, confirmPassword trùng password và `registrationToken` còn hiệu lực. fullName, dob, gender và phoneNumber là tùy chọn. Dob không được ở tương lai; gender là `Male`, `Female`, `Other` hoặc `PreferNotToSay`; phoneNumber có 6–15 chữ số, có thể bắt đầu bằng `+`. Username unique không phân biệt hoa thường. Không gửi role/organizationId để cấp quyền.

FE nhập form trước, giữ trong bộ nhớ rồi chuyển sang OTP. Nút “Xác thực và đăng ký” gọi verify-otp rồi gửi toàn bộ form + proof tới register, không phải gửi password tới verify-otp. `request-otp` và `resend-verification` trả **409 EMAIL_EXISTS** với `errors.email` cho account tồn tại (normalize trim/lowercase, kể cả inactive/deleted); không enqueue mã. Form sai trả 400 theo field và chưa consume proof. Đây là quyết định báo email trùng; forgot-password vẫn trả 202 chung. Không lưu password trong URL/web storage. [JSON và test Swagger đăng ký/PayOS](registration-payos-manual-test.md).

BE hash password vào `users.password_hash`, không tạo tài khoản email/password trên Firebase. Trả **201 AccountResponse**, chưa đăng nhập; gọi login tiếp theo:

Tài khoản tự đăng ký chỉ được ghi sau khi token OTP được consume; user mới đã có `email_verified_at`, vì vậy login không phải chờ worker xác minh. Trang `/check-email/` gọi `request-otp` lần đầu, `resend-verification` khi bấm gửi lại mã, rồi lưu `registrationToken` trong `sessionStorage` của cùng origin cho tới khi client gửi nó kèm request register. `POST /api/auth/verify-email` được giữ, đánh dấu deprecated, để xác minh link của account pending từ luồng cũ.

```json
{
  "id": "11111111-1111-4111-8111-111111111111",
  "email": "trainee@example.com",
  "fullName": "Nguyen Van A",
  "role": "Trainee",
  "organizationId": null,
  "username": "nguyen.van.a"
}
```

Lỗi: 400 VALIDATION_ERROR, 409 EMAIL_EXISTS hoặc USERNAME_EXISTS. AccountResponse từ nguồn tạo khác có thể có fullName/username null.

`POST /api/auth/register` là alias tương thích cho request Trainee cùng contract. `POST /api/auth/register/organization` nhận email/password/confirmPassword, organizationName, organizationAddress, organizationPhoneNumber và các trường hồ sơ tùy chọn fullName/dob/gender/phoneNumber; backend tạo owner `OrganizationUser` cùng organization trong một transaction.

### 2.2 Login local

```json
{ "email": "trainee@example.com", "password": "Example-Password-2026!" }
```

Email được chuẩn hóa; password login không rỗng, tối đa 128, không áp lại minimum 6 như lúc tạo/reset. Response:

```json
{
  "accessToken": "<Fire3D JWT>",
  "refreshToken": "<refresh token>",
  "user": {
    "id": "11111111-1111-4111-8111-111111111111",
    "email": "trainee@example.com",
    "fullName": "Nguyen Van A",
    "role": "Trainee",
    "organizationId": null
  }
}
```

LoginResponse **không có accessTokenExpiresAt/refreshTokenExpiresAt**. Lỗi: 400 VALIDATION_ERROR; 401 INVALID_CREDENTIALS cho sai email/password hoặc chưa có hash local; 403 ACCOUNT_DISABLED khi password đúng nhưng tài khoản/tổ chức không hợp lệ.

### 2.3 Google qua Firebase

Frontend đăng nhập Google bằng Firebase SDK, lấy Firebase ID token rồi POST login-firebase. Body là **JSON string**, không phải object idToken hoặc Google access token:

```json
"<Firebase ID token from Google sign-in>"
```

BE dùng Firebase Admin SDK kiểm chữ ký/expiry/revocation, `email_verified=true` và `firebase.sign_in_provider=google.com`; không dùng email claim đơn lẻ làm bằng chứng Google. Input rỗng/quá 16384 ký tự: 400; token/provider sai, expired hoặc revoked: 401 INVALID_FIREBASE_TOKEN. Deadline 15 giây, timeout/lỗi mạng/certificate fetch trả 503 GOOGLE_PROVIDER_UNAVAILABLE; request cancellation được giữ nguyên. Lỗi/log không chứa token, claim hoặc thông điệp nhạy cảm của SDK.

- UID đã liên kết: dùng hồ sơ/role DB.
- UID/email mới: trả `OnboardingRequired`, chưa tạo user hay organization cho tới khi luồng onboarding được triển khai.
- Email thuộc tài khoản khác/chưa liên kết UID này: 409 ACCOUNT_LINK_REQUIRED; không tự ghép chỉ vì trùng email.
- UID đã thay đổi trong lúc lấy khóa: 409 ACCOUNT_CHANGED; tài khoản/tổ chức bị khóa 403 ACCOUNT_DISABLED.
- Dưới khóa lifecycle/user, BE kiểm lại cả user ID sở hữu UID, role/tenant và pending legacy trước khi ghi session/audit. Account pending chưa verified/hết hạn vẫn trả403 EMAIL_NOT_VERIFIED/REGISTRATION_EXPIRED.

Google identity mới trả `{ status: "OnboardingRequired" }` và không tạo tài khoản. UID đã liên kết trả `{ status: "Authenticated", authentication: TokenResponse }`. Chưa có onboarding token/endpoint để người dùng hoàn tất chọn Trainee/OrganizationUser.

Response thành công cho UID đã liên kết là GoogleExchangeResponse, `authentication` chứa TokenResponse **không có expiresAt**:

```json
{
  "status": "Authenticated",
  "authentication": {
    "accessToken": "<Fire3D JWT>",
    "refreshToken": "<refresh token>",
    "user": {
      "id": "11111111-1111-4111-8111-111111111111",
      "email": "trainee@example.com",
      "fullName": "Nguyen Van A",
      "role": "Trainee",
      "organizationId": null
    }
  }
}
```

TTL token theo cấu hình Jwt của môi trường. Chưa có API liên kết Google vào tài khoản local có sẵn.

### 2.4 Refresh, logout, me, devices

`POST /api/auth/logout-all` requires Bearer authentication and revokes all refresh-token families for the current user, disables every current push binding, writes an audit record, and returns `204`. A valid access token is rejected after the transaction commits.

`PATCH /api/auth/me` requires `If-Match` from `GET /api/auth/me`. It can patch `fullName`, `username`, `dob` (`yyyy-MM-dd`), `gender`, and `phoneNumber`. For `dob`, `gender`, and `phoneNumber`, omitting a field preserves it while sending `null` clears it. Future dates and malformed phone numbers are rejected with field-level `VALIDATION_ERROR`.

`PUT /api/auth/devices` requires the authenticated user plus `X-Installation-Key`, a base64url-encoded 32-byte client secret. The server stores only its SHA-256 hash. Request body uses `deviceUuid`, optional `fcmToken`, `deviceModel`, `osVersion`, and `appVersion`; it rejects whitespace/control characters and values over their documented limits. A FCM token already actively bound to another user returns `409`.

Refresh không cần access token:

```json
{ "refreshToken": "<latest refresh token>" }
```

Token không trống, tối đa 256. Thành công trả TokenResponse gồm accessToken/refreshToken/user, không có expiresAt. Refresh luân chuyển token; lưu cả cặp mới, tránh nhiều request refresh đồng thời. Không kéo dài thời hạn tuyệt đối của family. Token không hợp lệ trả401 INVALID_REFRESH_TOKEN; replay token đã consume/revoke thu hồi cả family.

Logout không body, cần Bearer, trả 204 và thu hồi family phiên hiện tại; `/api/auth/logout-all` thu hồi toàn bộ family và vô hiệu push bindings. `GET /api/auth/me` trả AccountResponse cùng `ETag: "<profileRevision>"`; `PATCH /api/auth/me` nhận header đó trong `If-Match`, sửa fullName/username/dob/gender/phoneNumber, rồi trả ETag mới. Avatar hỗ trợ `POST /api/me/avatar/upload` nhận `multipart/form-data` với field `file` (JPEG/PNG/WebP, tối đa 5 MiB) để test S3 qua BE; route bắt buộc `If-Match`, rồi stream lên private staging object và hoàn tất theo cùng luồng Avatar. `POST /api/me/avatar/complete` và `DELETE /api/me/avatar` cũng yêu cầu cùng If-Match; thiếu trả 428, ETag cũ trả 412. `GET /api/organizations/me` trả OrganizationProfileResponse và ETag; PATCH chỉ sửa field được gửi, cần `If-Match`.

Devices upsert cho user hiện tại:

```json
{
  "deviceUuid": "11111111-1111-4111-8111-111111111111",
  "fcmToken": null,
  "deviceModel": "Test device",
  "osVersion": "Windows 11"
}
```

Actor và session family lấy từ JWT; FE không gửi userId. Bearer và X-Installation-Key cùng bắt buộc; FE sinh/lưu UUID và secret 32 byte base64url cho installation. FCM token bị trùng binding hoạt động trả 409 FCM_TOKEN_ALREADY_BOUND; proof sai 403 INSTALLATION_KEY_INVALID. Handler kiểm lại family/lifecycle dưới khóa. Optional metadata nullable; 200 chỉ chứng minh binding được lưu, chưa chứng minh FCM delivery. Đăng ký device lại sau logout-all khi login mới để bật push.

### 2.5 Forgot/reset password qua Mailgun

Forgot body:

```json
{ "email": "trainee@example.com" }
```

Sai email: 400 INVALID_EMAIL. Email đúng định dạng có/không tồn tại đều trả 202:

```json
{ "message": "Nếu tài khoản đủ điều kiện, hướng dẫn đặt lại mật khẩu sẽ được gửi đến email của bạn." }
```

202 chỉ xác nhận nhận yêu cầu; worker xử lý queue/gửi Mailgun sau đó. Có cooldown theo email; cần cấu hình DB, worker và dịch vụ email để nhận link. Frontend lấy token từ `/reset-password?token=<token>` rồi POST reset-password:

```json
{
  "token": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "newPassword": "New-Example-Password-2026!"
}
```

Phải dùng token thật từ email: 64 ký tự hex, hạn 30 phút, một lần; DB chỉ lưu hash token. Password 6–128, không chỉ khoảng trắng. Sai/hết hạn/đã dùng token: 400 INVALID_RESET_CODE; password sai: 400 INVALID_PASSWORD; thành công 204.

Reset chỉ áp dụng cho tài khoản đã có password local. Backend đổi hash local, tiêu thụ token, vô hiệu token reset còn lại và thu hồi phiên Fire3D trong transaction. Phải login lại; không trả JWT mới. Tài khoản Google-only không nhận reset token và không thể dùng reset để thêm password local. Không đổi password Google/Firebase; không nhận oobCode Firebase cũ. Không có endpoint đọc password/hash/reset token.

Chi tiết vận hành: [password-reset.md](password-reset.md).

### 2.6 Change password khi đã đăng nhập

Endpoint cần `Authorization: Bearer <Fire3D accessToken>` và không nhận `actorId` từ body:

```json
{
  "currentPassword": "Current-Example-Password-2026!",
  "newPassword": "New-Example-Password-2026!"
}
```

`currentPassword` không rỗng và tối đa 128 ký tự. `newPassword` phải dài 6–128 ký tự, không chỉ khoảng trắng và phải khác mật khẩu hiện tại. Sai mật khẩu hiện tại: 400 `INVALID_CURRENT_PASSWORD`; mật khẩu mới không hợp lệ: 400 `INVALID_PASSWORD`; trùng mật khẩu hiện tại: 400 `PASSWORD_UNCHANGED`; tài khoản/organization không còn hoạt động: 401 `UNAUTHORIZED`.

Thành công trả 204. Trong cùng transaction khóa lifecycle/user, BE kiểm lại family lấy từ JWT, account và organization rồi cập nhật `password_hash`, đánh dấu token reset local/legacy đã dùng, thu hồi toàn bộ refresh session và ghi audit. Family đã revoke trả401 ngay cả khi request đã qua middleware trước đó; phiên login mới không bị request cũ thu hồi. Legacy invalidation gọi gate giới hạn quyền, không DELETE lịch sử. Access JWT hiện tại sẽ không còn được chấp nhận; client phải đăng nhập lại. Endpoint không gửi email, không thay đổi password Google/Firebase và không nhận Firebase oobCode.

## 3. Accounts và Organizations — 8 endpoint

Tất cả cần Admin: thiếu JWT 401, sai role 403. Tạo/đổi trạng thái trả header X-Correlation-ID do server tạo để đối chiếu audit.

| Method | Path | Input | Thành công |
| --- | --- | --- | --- |
| POST | `/api/accounts` | CreateAccountRequest | 201 AccountResponse |
| GET | `/api/accounts` | Account filter | 200 Page<ManagedAccountResponse> |
| GET | `/api/accounts/{id}` | ID | 200 ManagedAccountResponse |
| PATCH | `/api/accounts/{id}/status` | isActive | 200 ManagedAccountResponse |
| POST | `/api/organizations` | name, slug | 201 OrganizationResponse |
| GET | `/api/organizations` | Organization filter | 200 Page<OrganizationResponse> |
| GET | `/api/organizations/{id}` | ID | 200 OrganizationResponse |
| PATCH | `/api/organizations/{id}/status` | isActive | 200 OrganizationResponse |

### Accounts

```json
{
  "email": "editor@example.com",
  "password": "Example-Password-2026!",
  "fullName": "Building Editor",
  "role": "OrganizationUser",
  "organizationId": "22222222-2222-4222-8222-222222222222"
}
```

Email tối đa 254 hợp lệ; password 6–128 không chỉ khoảng trắng; fullName tùy chọn tối đa 200; role bắt buộc. OrganizationUser phải có organizationId hoạt động; role khác phải null/không gửi. Admin tạo cũng hash password local. Lỗi 400 VALIDATION_ERROR/INVALID_ORGANIZATION, 409 EMAIL_EXISTS, 403 FORBIDDEN.

POST trả AccountResponse; GET/PATCH trả ManagedAccountResponse: các field AccountResponse cộng isActive, lastLoginAt nullable, createdAt, updatedAt.

GET list nhận page/pageSize/search/isActive/role/organizationId. Detail không thấy trả 404. PATCH status dùng body sau cho cả accounts/organizations:

```json
{ "isActive": false }
```

isActive bắt buộc, không null. Admin tự vô hiệu mình: 409 SELF_DEACTIVATION. Trạng thái đã có vẫn trả DTO thành công. Chưa có API đổi role/tổ chức, sửa email/profile hay hard-delete.

### Organizations

```json
{ "name": "Fire3D Demo Organization", "slug": "fire3d-demo" }
```

Name trim, bắt buộc, tối đa 200; slug trim/lowercase tối đa 100, regex `^[a-z0-9]+(-[a-z0-9]+)*$`. Trùng slug: 409 SLUG_EXISTS; sai input: 400 VALIDATION_ERROR.

OrganizationResponse: id, name, slug, isActive, createdAt, updatedAt. List nhận page/pageSize/search/isActive. Chưa có route sửa tên/slug/xóa. Vô hiệu tổ chức ảnh hưởng quyền OrganizationUser, không xóa dữ liệu nghiệp vụ.

## 4. Buildings và Revision detail — 9 endpoint

| Method | Path | Quyền | Thành công |
| --- | --- | --- | --- |
| POST | `/api/buildings` | Building CRUD | 201 BuildingResponse |
| GET | `/api/buildings` | Building CRUD | 200 Page<BuildingSummaryResponse> |
| GET | `/api/buildings/{id}` | Building CRUD | 200 BuildingResponse |
| PUT | `/api/buildings/{id}` | Building CRUD | 200 BuildingResponse |
| DELETE | `/api/buildings/{id}` | Building CRUD | 200 BuildingSummaryResponse |
| POST | `/api/buildings/{id}/revisions/upload-url` | Editor | 201 InitiateIfcUploadResponse |
| GET | `/api/buildings/{id}/revisions` | Editor | 200 Page<RevisionResponse> |
| GET | `/api/buildings/{id}/trainings` | Editor | 200 TrainingDto[] |
| GET | `/api/revisions/{id}` | Editor | 200 RevisionResponse |

POST/PUT building cùng body; PUT không phải partial PATCH:

```json
{
  "name": "Demo Building",
  "buildingType": "Office",
  "totalFloors": 5,
  "location": {
    "address": "Demo address", "city": "Ho Chi Minh City", "district": "Demo district",
    "latitude": 10.8, "longitude": 106.7, "geojson": null
  },
  "contact": {
    "contactName": "Demo Contact", "contactRole": "Manager", "phone": null,
    "email": "contact@example.com", "isPrimary": true
  }
}
```

Name bắt buộc tối đa 200 sau trim, totalFloors >= 1. buildingType/location/contact nullable. Nếu có contact, contactName trắng/null bị trả 400 trước Trim. Tọa độ nullable decimal; geojson là chuỗi, không phải object. Chưa validate đầy đủ tọa độ/GeoJSON/contact; không giả định mọi DB exception đều chuyển thành 400.

PUT với location/contact null giữ nested data hiện có, không xóa. Query organizationId chỉ chọn scope đích, vẫn kiểm quyền từ DB; mặc định tenant claim. OrganizationId chưa có trong body.

BuildingResponse: id, name, buildingType, totalFloors, isActive, organizationId, createdAt, updatedAt, location, contact. Nested response thêm id vào các trường request tương ứng. BuildingSummaryResponse: id, name, buildingType, totalFloors, isActive, createdAt.

List nhận page/pageSize/search/isActive. DELETE chỉ đặt isActive=false, **200 DTO, không 204**, không xóa vật lý. Chưa có route bật lại building.

Revision list nhận page/pageSize; detail nhận ID. Editor scope, Trainee 403, tài nguyên không thấy/ngoài phạm vi hoặc building archive có thể 404. RevisionResponse: id, buildingId, versionLabel, status, createdAt, sourceDocument nullable. SourceDocumentResponse: id, originalFilename, fileSizeBytes, quarantineStatus, createdAt; không object key/download URL.

Trainings trả mảng, không phân trang: id, releaseId, name, description nullable, status, startDate/endDate nullable, allowedModes (string[]), createdAt. Query lọc training Active và release Published, chưa lọc khoảng ngày. Đây là Editor API, không phải danh sách học public/Trainee.

Upload-url cũ là route tương thích và dùng cùng `InitiateIfcUploadCommand` với `/api/buildings/{buildingId}/ifc`. Hai route nhận cùng body, tạo revision Draft và trả cùng response `revisionId`, `uploadUrl`, `objectKey`; client mới nên dùng route IFC. Không gọi đồng thời cả hai route cho cùng một file vì mỗi lần gọi tạo một revision mới.

## 5. IFC commands — 6 endpoint

Tất cả cần Editor, rate limit administration. Storage phải được cấu hình thật; có route không đồng nghĩa worker IFC đã hoạt động.

| Method | Path | Input | Thành công |
| --- | --- | --- | --- |
| POST | `/api/buildings/{buildingId}/ifc` | InitiateIfcUploadRequest | 201 InitiateIfcUploadResponse |
| POST | `/api/revisions/{revisionId}/upload-complete` | FinalizeIfcUploadRequest | 204 |
| POST | `/api/revisions/{revisionId}/process` | Không body | 202 {jobId} |
| POST | `/api/processing-jobs/{jobId}/retry` | requestId, reason | 202 RetryProcessingJobResponse |
| POST | `/api/revisions/{revisionId}/confirm-for-training` | Không body | 200 rỗng |
| POST | `/api/revisions/{revisionId}/reviews` | scenarioVersionId, action, validationRunId, message | 201 reviewId |

### 5.1 Initiate → upload → finalize

Initiate yêu cầu size dương, tên file kết thúc .ifc (không phân biệt hoa/thường), versionLabel không trống:

```json
{ "fileSizeBytes": 1048576, "originalFilename": "demo-building.ifc", "versionLabel": "v1" }
```

Không gửi MIME/hash ở bước initiate. Response:

```json
{
  "revisionId": "33333333-3333-4333-8333-333333333333",
  "uploadUrl": "https://storage.example.com/presigned-upload",
  "objectKey": "<server-generated object key>"
}
```

Upload bytes trực tiếp lên presigned URL, với Content-Type application/octet-stream như lúc ký URL; thời hạn URL được yêu cầu 60 phút. Không gửi multipart file vào BE hay JWT Fire3D sang storage. Giữ revisionId/objectKey cho finalize:

```json
{
  "objectKey": "<objectKey from initiate>",
  "fileSizeBytes": 1048576,
  "mimeType": "application/octet-stream",
  "sha256Hash": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "originalFilename": "demo-building.ifc"
}
```

Hash cần tính từ file thật. Handler kiểm objectKey/hash không trống, size dương và object tồn tại/đúng size trên storage. **Chưa kiểm hash nội dung file tại handler**, chưa ràng buộc đầy đủ objectKey với lần initiate. Đây là khoảng trống validation, không phải quyền dùng key bất kỳ.

Lỗi chính: 400 input sai; 404 building/revision không thấy hoặc ngoài phạm vi; 409 đã finalize; 422 storage không xác minh được object. Initiate ghi revision trước khi lấy URL, nên lỗi storage có thể để lại revision; không coi retry POST là idempotent.

### 5.2 Process, retry, confirm

Process cần SourceDocument; thiếu source 400; đã có job cho revision 409. Contract thành công là 202 với jobId và Location trỏ job detail. **Xem blocker mục 9 trước khi coi process chạy được**. Dùng GET job để poll, không coi 202 là IFC đã xử lý xong.

Retry job Failed:

```json
{
  "requestId": "44444444-4444-4444-8444-444444444444",
  "reason": "Retry after fixing worker configuration"
}
```

requestId là UUID khác Guid.Empty; reason không trống, tối đa 1000, được trim. Response gồm jobId và outcome `Requeued`/`AlreadyRequeued`, status 202 và Location job detail. Gửi lại cùng key/input không requeue lặp. Key cũ/input khác: 409 IDEMPOTENCY_CONFLICT. Key mới khi job không thể retry: 409 JOB_CONFLICT hoặc JOB_NOT_CLAIMABLE. Tạo key mới cho một lần retry chủ động mới.

Confirm chuyển ReadyForScenario → ConfirmedForTraining. Trạng thái khác: 400 INVALID_STATE; không thấy/ngoài phạm vi: 404; thành công 200 rỗng. Không tự tạo release/training.

## 6. IFC queries — 8 endpoint

Tất cả cần Editor; list nhận page/pageSize. Sai filter/Guid.Empty 400, sai role 403, không thấy/ngoài tổ chức 404.

| Method | Path | Thành công |
| --- | --- | --- |
| GET | `/api/revisions/{revisionId}/processing-logs` | 200 Page<RevisionProcessingLogResponse> |
| GET | `/api/revisions/{revisionId}/processing-jobs` | 200 Page<ProcessingJobResponse> |
| GET | `/api/processing-jobs/{jobId}` | 200 ProcessingJobDetailResponse |
| GET | `/api/validation-runs/{validationRunId}` | 200 ValidationRunResponse |
| GET | `/api/processing-jobs/{jobId}/qa` | 200 JobQaResponse |
| GET | `/api/revisions/{revisionId}/issues` | 200 Page<RevisionIssueResponse> |
| GET | `/api/revisions/{revisionId}/artifacts` | 200 Page<RevisionArtifactResponse> |
| GET | `/api/revisions/{revisionId}/bim-facts` | 200 Page<BimFactResponse> |

| DTO | Thuộc tính JSON |
| --- | --- |
| ProcessingJobResponse | id, revisionId, sourceDocumentId, scenarioVersionId nullable, kind, status, createdAt |
| ProcessingJobDetailResponse | job (ProcessingJobResponse), inputHash, currentAttemptId nullable, currentAttempt nullable |
| ProcessingAttemptResponse | id, attemptNumber, status, toolchainVersion, startedAt, finishedAt nullable, outputHash nullable |
| ValidationRunResponse | id, revisionId, processingJobId, processingAttemptId, artifactId nullable, scenarioVersionId nullable, scope, validatorVersion, status, summary (JSON), startedAt/finishedAt nullable, createdAt |
| JobQaResponse | jobId, currentAttemptId nullable, validationRuns (Page<ValidationRunResponse>) |
| RevisionIssueResponse | id, revisionId, validationRunId, processingAttemptId, artifactId nullable, issueCode, severity, status, message, evidence (JSON), isCurrentAttempt, createdAt |
| RevisionArtifactResponse | id, revisionId, jobId, attemptId, artifactType, sha256Hash, metadata (JSON), isRuntimeReady, isCurrentAttempt, createdAt |
| BimFactResponse | id, revisionId, ifcGlobalId, entityType, propertyPath, value (JSON nullable), sourceHash, qualityFlags (JSON), createdAt |

Job mới có thể chưa có currentAttempt. QA chỉ lấy validation của attempt hiện tại; mảng rỗng **không phải Passed**. Validation theo ID có thể là bản lịch sử, không chứng minh attempt hiện tại đạt QA.

Issues/artifacts có dữ liệu lịch sử: đọc isCurrentAttempt. isRuntimeReady không phải URL tải file. Các DTO đọc không có raw object key, signed download URL, worker lease/credential. Không hardcode schema bên trong summary/metadata/evidence/qualityFlags khi DTO chỉ cam kết kiểu JSON.

## 7. Scenario, catalog, playtest and review — 14 endpoints

Tất cả cần Editor. Đây là luồng tác giả thiết kế/thử kịch bản, không phải phiên học và thống kê Trainee.

| Method | Path | Input | Thành công |
| --- | --- | --- | --- |
| POST | `/api/scenarios` | buildingId, name | 201 {id} |
| GET | `/api/buildings/{buildingId}/scenarios` | page, pageSize | 200 Page<ScenarioSummaryResponse> |
| GET | `/api/scenarios/{scenarioId}` | Không body | 200 ScenarioDetailResponse |
| POST | `/api/scenarios/{scenarioId}/draft` | revisionId | 201 {id} |
| GET | `/api/scenario-drafts/{draftId}` | Không body | 200 ScenarioDraftResponse + ETag |
| PUT | `/api/scenario-drafts/{draftId}` | State + If-Match | 204 + ETag |
| POST | `/api/scenario-drafts/{draftId}/validate` | Không body | 200 ScenarioDraftValidationResponse |
| POST | `/api/scenario-drafts/{draftId}/snapshot` | Không body | 201 {id} |
| GET | `/api/scenarios/{scenarioId}/versions` | page, pageSize | 200 Page<ScenarioVersionSummaryResponse> |
| GET | `/api/scenario-versions/{versionId}` | Không body | 200 ScenarioVersionDetailResponse |
| GET | `/api/scenario-interactions/catalog` | Không body | 200 RuntimeCatalogDto[] |
| POST | `/api/scenarios/{scenarioId}/playtests` | Query buildingId + body | 201 {id} |
| POST | `/api/playtests/{playtestId}/start` | Không body | 200 rỗng |
| POST | `/api/revisions/{revisionId}/reviews` | scenarioVersionId, validationRunId, reviewMessage, annotationSetId nullable | 201 {id} |

### 7.1 Scenario/draft

Tạo scenario với building có thật trong phạm vi và name không trống:

```json
{ "buildingId": "55555555-5555-4555-8555-555555555555", "name": "Evacuation drill" }
```

Tạo draft từ scenario:

```json
{ "revisionId": "33333333-3333-4333-8333-333333333333" }
```

Revision phải cùng building với scenario; sai 400 VALIDATION_ERROR; scenario không thấy/ngoài phạm vi 404. Cả hai POST trả id và Location.

Editor dùng GET building scenarios để mở danh sách, GET scenario để xem metadata, GET draft để lấy state đã lưu và ETag, và GET versions/version detail để xem lịch sử snapshot bất biến. OrganizationUser chỉ thấy tenant của mình; PlatformAdmin có scope toàn hệ thống; resource không thấy hoặc ngoài scope trả 404. List dùng `page` (mặc định 1) và `pageSize` (mặc định 20, tối đa 100).

### 7.2 Lưu draft và snapshot

PUT nhận trực tiếp ScenarioDraftStateDto, không bọc state hoặc expectedVersion. Header `If-Match` chứa version uint, có thể trong dấu ngoặc kép. Body:

```json
{
  "spawnPoints": [ { "x": 0, "y": 0, "z": 0, "rotation": 0 } ],
  "hazards": [
    {
      "id": "hazard-1", "type": "Fire",
      "position": { "x": 2, "y": 0, "z": 3, "rotation": 0 },
      "intensity": 0.5, "activationTime": 10
    }
  ],
  "scoringConfig": { "baseScore": 100, "timeLimitSeconds": 300, "penaltyPerMistake": 10 },
  "routingConfig": { "evacuationRoutes": [] }
}
```

Type Fire, các số và route chỉ minh họa DTO; chưa có validation đầy đủ catalog/đơn vị/khoảng giá trị/khả năng chạy Unity. Không thay bằng nodes/edges tự định nghĩa.

- Thiếu/sai If-Match, ví dụ `*` hoặc weak ETag: 412.
- Version cũ/concurrent update: 409 CONFLICT.
- Thành công: 204 với `ETag: "<newVersion>"`; giữ version mới cho lần lưu tiếp.

Version dựa trên PostgreSQL xmin, **không mặc định là 1**. GET draft trả state và `ETag: "<version>"`; dùng ETag đó cho lần PUT tiếp theo.

Snapshot không body, trả 201 id ScenarioVersion. Đây là snapshot state, không publish hoặc tự tạo runtime package.

`POST /api/scenario-drafts/{draftId}/validate` trả `{draftId, version, isValid, issues}`. Mỗi issue có `code`, đường dẫn JSON `path`, và `message`. Hiện endpoint đồng bộ kiểm tra cấu trúc draft: spawn point, hazard/position, scoring và evacuation route. Nó **không** xác nhận geometry IFC hoặc runtime capability vì hai kiểm tra này cần worker pipeline/catalog thực tế; response hợp lệ không phải confirmation-for-training.

`POST /api/revisions/{revisionId}/reviews` chỉ tạo review `Rejected` cho một cặp revision–scenario version. Body phải đưa validation run cùng cặp đó và lý do 1–4000 ký tự; annotation set là tùy chọn nhưng nếu có phải thuộc revision. Server ghi review và audit trong một transaction, không chuyển trạng thái chung của revision sang Rejected. Version đã có review trả 409 `CONFLICT`.

### 7.3 Catalog/playtest

Catalog trả mảng `{runtimeVersion, protocolVersion, manifestSchemaVersion, capabilities}`; capabilities là JSON. Dùng giá trị catalog/package thực tế khi tích hợp runtime.

Prepare cần `?buildingId=<building UUID>` cùng scenarioId trên path:

```json
{
  "revisionId": "33333333-3333-4333-8333-333333333333",
  "scenarioDraftId": null,
  "scenarioVersionId": "66666666-6666-4666-8666-666666666666",
  "packageHash": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "protocolVersion": "<from catalog>",
  "manifestSchemaVersion": "<from catalog>",
  "runtimeVersion": null
}
```

Handler yêu cầu ít nhất một draftId/versionId; client nên gửi đúng một. Hiện chưa reject cả hai và store ưu tiên kiểm version. Revision/scenario phải thuộc building; draft/version thuộc scenario. Thiếu nguồn 400, quan hệ không thấy 404. Package hash phải lấy từ package thật.

Prepare hiện trả 503 `ENTITLEMENT_UNAVAILABLE`: API đang fail-closed cho đến khi Building entitlement/trial gate được migrate. Start giữ 404 cho session không thuộc actor; một session Created hợp lệ từ dữ liệu cũ mới có thể chuyển Created → Running. Không trả launch token, manifest hay URL package. 200 không chứng minh Unity đã khởi chạy hoặc đã ghi kết quả huấn luyện.

## 8. Release lifecycle — 4 endpoint

| Method | Path | Quyền | Input | Thành công |
| --- | --- | --- | --- | --- |
| POST | `/api/releases` | Editor | BuildReleaseRequest | 201 ReleaseResponse + Location |
| GET | `/api/releases/{releaseId}` | Editor | Không body | 200 ReleaseResponse |
| POST | `/api/releases/{releaseId}/publish` | Editor | Không body | 204 |
| POST | `/api/releases/{releaseId}/revoke` | Editor | `{ "reason": "..." }` | 204 |

`POST /api/releases` ghi nhận một Unity/package build đã hoàn tất và tạo nguyên tử release trạng thái Built, package metadata và audit. Request pin revision, scenarioVersion, ConfirmForTraining review, candidate artifact, safetyThresholds JSON object, manifest/package private object key, hai SHA-256 64 ký tự, packageSizeBytes dương, minRuntimeVersion, schemaVersion và buildTarget. Revision phải ConfirmedForTraining; Building/tổ chức phải active; review và artifact phải khớp revision/version. Một cặp revision + scenarioVersion chỉ có một release; trùng trả 409 RELEASE_EXISTS.

Create/build được gộp vì schema bắt buộc release mới được tạo ở trạng thái Built; API không chạy Unity trong request. Worker phải upload và kiểm chứng artifact trước khi gọi API này. GET trả release cùng package metadata, không trả signed download URL. Publish hiện trả 503 `PUBLISH_GATE_UNAVAILABLE` cho đến khi entitlement, validation, issue, runtime compatibility và Training gate được triển khai. Revoke nhận reason 1–1000 ký tự, cho Built/Published → Revoked, ghi actor/time/reason/audit; gọi lại release đã Revoked vẫn trả 204.

Các lỗi chung: 400 validation, 401 account không hợp lệ, 403 Trainee, 404 không thấy/khác tenant, 409 state hoặc release đã tồn tại. PlatformAdmin có thể đọc/thao tác liên tổ chức theo policy hiện tại. Publish vẫn phụ thuộc gate QA/runtime/entitlement/Training của schema triển khai; chưa tự tạo Training hoặc khởi chạy Unity build job.

## 9. Giới hạn thấy khi đối chiếu source

| Phần | Hiện trạng và ảnh hưởng |
| --- | --- |
| Building CRUD | Mutation kiểm DB actor/tenant và audit atomic; admin dùng query organizationId. Read list/detail vẫn dựa scope organization claim, body chưa nhận organizationId; chưa coi admin read toàn nền tảng đã hoàn thiện. |
| Upload-url cũ | Đã là alias tương thích của initiation IFC; client mới dùng `/api/buildings/{buildingId}/ifc` |
| IFC finalize | Chưa ràng buộc đủ key với revision/upload; chưa kiểm hash nội dung; validation MIME/tên/hash hạn chế |
| IFC process | Process/confirm đã Include Building; Process gọi enqueue_integration_outbox_event schema 1, hash canonical JSONB và tenant suy từ DB, job/audit/outbox atomic. Migration AddIfcIntegrationOutbox giao table/function/grants còn thiếu. Dispatcher/worker delivery, attempt/result gates và provenance production chưa hoàn chỉnh. Xem [IFC outbox](ifc-outbox.md). |
| Draft editor | GET draft state/version đã có. Kiểm tra response ETag/xmin trước khi tích hợp; không coi đây là API còn thiếu. |
| Playtest prepare | Runtime hiện fail-closed 503 `ENTITLEMENT_UNAVAILABLE`; entitlement/trial, compatibility và launch grant chưa triển khai. Store legacy không được DI đăng ký. |
| Playtest start | Runtime kiểm owner session trước khi delegate trạng thái/audit; chưa trả launch grant. |
| Release/training | Đã có create-Built/read/revoke; runtime publish fail-closed 503 `PUBLISH_GATE_UNAVAILABLE` cho đến khi có gate. Còn thiếu package-build job và vòng đời Training/session. |
| Auth | Form → OTP → proof → register → login cho Trainee/OrganizationUser; OrganizationUser route chưa nhận username cá nhân. Request/verify OTP không tạo identity; verify-email link chỉ cho pending legacy. Profile cá nhân/tổ chức và avatar mutation dùng ETag; logout-all đã có. AvatarService reserve candidate trước conditional S3 copy, có cleanup/recovery source; provider/deployment cần kiểm riêng. Google onboarding completion/link còn thiếu. |
| Token response | Login local, Firebase login và refresh đều không trả expiresAt |
| Device | Installation proof và family session được kiểm khi bind/revoke; FCM send chỉ dùng binding active. Không coi test mock là bằng chứng FCM production delivery. |

Số endpoint không phản ánh mức độ hoàn thiện luồng. Cập nhật tài liệu không thay source, chạy migration hoặc xác nhận kết nối dịch vụ thực tế.

### Khoảng cách tới contract v7

| Capability đích | Việc cần triển khai trong checklist |
| --- | --- |
| Organization Library / Admin approval | LIBRARY-01, APPROVAL-01: template/rubric/thiết bị version hóa; duyệt scenario/rubric đúng hash, tách readiness kỹ thuật. |
| Private Building | ACCESS-01: grant gắn account/access revision, rotate/revoke mã và visibility vô hiệu grant cũ; QR không tạo tenant membership. |
| Seats / session / Assessment | CAPACITY-01, SESSION-01, ASSESSMENT-01: distinct user/Building/kỳ tại start, pin entitlement/review/rubric, kết quả riêng completion; sync sau expiry/revoke. |
| Gói 6/12 tháng / AI prepaid | BILLING-02, AI-01: snapshot seats/quota/policy, provisioning replay sau expiry; top-up không gia hạn Building, không invoice AI cuối kỳ. |
| Learner-safe RAG | RAG-01: chỉ name/objectives/instructions approved/published; kiểm quyền mỗi retrieval và chặn AI trong Assessment. |

Chi tiết và acceptance tại [implementation checklist](api-implementation-checklist.md). Capability chưa có route/DTO được ghi là thiết kế đích; method/path/payload thuộc task implementation sau. Learn không có approval riêng; approval v7 áp dụng scenario/rubric.

## 10. Checklist tích hợp và test tay

### Auth local

1. Form → request-otp → verify-otp nhận proof → register toàn bộ form/proof → 201 AccountResponse, không token. Trainee organizationId null; OrganizationUser tạo organization atomic. Request/verify chưa tạo identity; email đã có trả 409 EMAIL_EXISTS.
2. Login sai → 401; đúng → 200 LoginResponse không expiresAt.
3. Me không Bearer → 401; Fire3D JWT hợp lệ → đúng tài khoản.
4. Refresh → lưu cặp mới. Test replay riêng vì token cũ có thể thu hồi family.
5. Logout → 204; phiên vừa logout không gọi API bảo vệ được.
6. Forgot email hợp lệ có/không tồn tại → cùng 202. Xác minh worker/Mailgun riêng.
7. Reset token thật → 204; password cũ không login được, password mới được; token dùng lại → 400; phiên cũ bị thu hồi.

### Google, admin và tenant

1. Firebase Google → JSON string ID token: UID đã link trả Authenticated; UID mới trả OnboardingRequired, chưa tạo account. Firebase email/password provider không dùng được ở route này; completion/link còn thiếu.
2. Local account trùng email Google chưa liên kết → 409, không tự ghép/nâng quyền.
3. Admin tạo organization → 201; tạo OrganizationUser → 201; login local tài khoản vừa tạo.
4. Trainee gọi Editor → 403; OrganizationUser đọc revision/job tổ chức khác → 404; kiểm PlatformAdmin riêng cho Editor.
5. Admin tự deactivate → 409; deactivate organization → OrganizationUser mất điều kiện truy cập.

### IFC và authoring

1. Chuẩn bị OrganizationUser, building, storage; initiate → upload bytes → finalize. Sai object/file → lỗi tương ứng.
2. Sửa blocker process/worker rồi mới test chuỗi process → poll → QA/artifacts/BIM facts; không dùng QA lịch sử làm kết luận hiện tại.
3. Retry Failed cùng requestId → không requeue lặp; key cũ/reason khác → 409.
4. GET draft hiện trả state và ETag; dùng ETag nhận được để test create → edit. Thiếu header bị từ chối, ETag cũ không được ghi đè, lưu đúng trả 204 cùng ETag mới.
5. Test playtest/package/entitlement/publish với DB/runtime thật sau khi hoàn thiện; không đồng nhất playtest và Trainee session.

## 11. Nguồn đối chiếu và bảo trì

- Routes/status/quyền controller: [Controllers](../Fire3D/Fire3D.API/Controllers).
- Auth DTO/handler: [Authentication](../Fire3D/Fire3D.Application/Authentication).
- Quản trị: [AdministrationContracts.cs](../Fire3D/Fire3D.Application/Administration/AdministrationContracts.cs).
- Building/revision: [BuildingContracts.cs](../Fire3D/Fire3D.Application/Buildings/BuildingContracts.cs).
- IFC: [Application/Ifc](../Fire3D/Fire3D.Application/Ifc), [IfcWriteStore.cs](../Fire3D/Fire3D.Infrastructure/Ifc/IfcWriteStore.cs).
- Draft: [ScenarioDraftStateDto.cs](../Fire3D/Fire3D.Application/Scenarios/Dto/ScenarioDraftStateDto.cs).
- Playtest: [FailClosedPlaytestWriteStore.cs](../Fire3D/Fire3D.Infrastructure/Scenarios/FailClosedPlaytestWriteStore.cs).
- Publish: [FailClosedReleaseStore.cs](../Fire3D/Fire3D.Infrastructure/Releases/FailClosedReleaseStore.cs).

Khi đổi API, cập nhật method/path, permission, request/response, status, validation và ví dụ. Đối chiếu handler/store, không chỉ annotation Swagger. Tài liệu được kiểm danh mục route, JSON và liên kết nội bộ; không thay báo cáo integration test.

## Editor preview và annotations — bổ sung 2026-09-23

Cả ba operation yêu cầu Bearer Fire3D hợp lệ. Backend đọc lại account từ DB: OrganizationUser chỉ truy cập tenant của mình; PlatformAdmin được truy cập liên tổ chức; Trainee nhận 403. Account không hoạt động nhận 401; revision không tồn tại/ngoài tenant hoặc Building/organization ngừng hoạt động nhận 404. Response không cache.

| Method | Route | Request | Thành công |
|---|---|---|---|
| GET | `/api/buildings/{buildingId}/editor-preview?revisionId={revisionId}` | Bắt buộc chọn revision thuộc Building | 200 `EditorPreviewResponse` |
| GET | `/api/revisions/{revisionId}/annotations` | Không có body | 200 snapshot + header `ETag` |
| PUT | `/api/revisions/{revisionId}/annotations` | Body bên dưới; header `If-Match` lấy từ GET | 200 snapshot mới + `ETag` mới |

Preview trả `buildingId`, `revisionId`, `revisionStatus`, `status` (`Ready`/`NotReady`), `artifactId`, `attemptId`, `sha256Hash`, `downloadUrl`, `expiresAt`, `coordinateTransform`, `floors`, `semanticMapping`. Không trả storage key. Signed GET URL tồn tại 5 phút, chỉ ký khi artifact `preview_glb` thuộc current attempt của Geometry job, job/attempt đều Succeeded và input hash khớp; hash SHA-256 và metadata bắt buộc hợp lệ. Chưa đủ dữ liệu trả 200 `NotReady`, URL/expiry null. Sai/missing GUID đầu vào trả 400. Trạng thái Ready không cấp quyền publish hoặc training.

Worker contract hiện tại: metadata của chính artifact chứa `coordinateTransform` là mảng phẳng 16 số hữu hạn, `floors` là array, `semanticMapping` là object. Không ghép metadata từ revision khác. Worker chưa xuất các trường này thì preview vẫn NotReady. Backend chưa HEAD object S3 để xác minh file thực sự tồn tại; việc ký URL không chứng minh worker upload thành công.

Annotations GET khi chưa có bản lưu trả `version: 0`, `id: null`, `data: {"items":[]}`, header `ETag: "0"`. Snapshot có `revisionId`, `id`, `version`, `data`, `provenance`, `createdBy`, `createdAt`, `eTag`. PUT gửi:

```http
PUT /api/revisions/{revisionId}/annotations
Authorization: Bearer <accessToken>
If-Match: "0"
Content-Type: application/json
```

```json
{
  "items": [
    {
      "id": "3e63037d-1a9b-4d91-a311-c88caf34f966",
      "ifcGlobalId": "IFC-SPACE-1",
      "label": "Phòng kỹ thuật",
      "note": "Nhãn phục vụ editor"
    }
  ]
}
```

Tối đa 500 item, id GUID khác rỗng và không trùng; ifcGlobalId tối đa 255 ký tự và phải có trong bim_facts cùng revision; label bắt buộc ≤200 ký tự; note tùy chọn ≤2000 ký tự. Không nhận trường JSON ngoài contract. `items: []` tạo overlay rỗng mới, giữ nguyên lịch sử. Đây là nhãn/ghi chú, không thay đổi geometry, cấu hình exit hay artifact đã pin.

Thiếu If-Match: 428 `PRECONDITION_REQUIRED`; sai định dạng (wildcard/weak/multiple tag không được hỗ trợ): 400; ETag cũ: 412 `PRECONDITION_FAILED`; anchor không tồn tại trong revision: 400 `INVALID_ANCHOR`. Sau 412, FE GET lại và cho người dùng đối chiếu thay đổi, không tự ghi đè.

PUT khóa revision, kiểm tra version rồi append annotation_sets + audit_logs trong cùng transaction PostgreSQL. Lỗi audit rollback annotation. Mỗi bản cũ bất biến; chưa có event/outbox cho annotations vì chưa có downstream consumer trong phạm vi này. Không cần bảng mới nếu deployment đã có schema đích annotation_sets và các cột provenance của processing jobs/artifacts; chưa chạy migration Supabase trong task này.
