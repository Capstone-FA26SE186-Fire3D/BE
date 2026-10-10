# Fire3D — Hướng dẫn tích hợp API hiện tại

Phone cá nhân bổ sung: register Trainee/OrganizationUser/alias, Google completion và PATCH `/api/auth/me` trả409 `PHONE_NUMBER_EXISTS`/`errors.phoneNumber` khi trùng canonical giữa user. NULL tùy chọn; không unique chéo số tổ chức, không suy0…/+84…; conflict giữ proof/profile revision/audit. [Contract và test tay](personal-phone-uniqueness.md).

Cập nhật contract **04/10/2026** theo [Docs v7](../../Docs/schema_v7_contract.md) và rà source BE `e42a2eb`. Các mục endpoint/editor bắt đầu từ baseline `946017d` ngày 23/09, có bổ sung auth/billing tháng 10; số endpoint ở heading là danh mục lịch sử, cần kiểm controller khi tích hợp. Đây là hướng dẫn API source hiện có, không chứng minh toàn bộ capability v7 đã hoàn thành.

Các phần dưới có baseline lịch sử riêng; không dùng số endpoint cũ để suy mức hoàn thiện hiện tại. Bổ sung **02/10/2026**: catalog, quotation Building và enterprise contact request tại [billing.md](billing.md), gồm route/quyền, If-Match, Idempotency-Key, ví dụ và luồng checkout PayOS, webhook, entitlement/reconcile đã có code/test. Cập nhật **03/10/2026**: migration PayOS/email và login giới hạn quyền đã áp vào Supabase; API local tạo link/QR provider thật thành công, chưa chuyển tiền. Azure đã phục vụ Swagger/OpenAPI/return/cancel và CORS FE, nhưng worker/executor/webhook/Paid/provisioning deployment vẫn chưa nghiệm thu. Đối chiếu OpenAPI/source và [implementation checklist](api-implementation-checklist.md) khi tích hợp; endpoint tồn tại không chứng minh provider đã hoạt động.

### Giới hạn OpenAPI deploy đã kiểm tra ngày 03/10/2026

OpenAPI tại host Azure tự báo build `d785937e5948f31486df66740a1f31ccd9ce25a5`, có 118 operation HTTP GET/POST/PUT/PATCH/DELETE; đây là số metadata, không phải số capability đã hoàn tất. Server `/` dùng cùng HTTPS origin.

- Avatar upload đang mô tả `application/x-www-form-urlencoded` với thuộc tính IFormFile. Contract runtime cần `multipart/form-data`, field `file` và `If-Match`; Swagger chưa thể hiện nút file đúng.
- `UpdateCurrentProfileRequest` và `UpdateOrganizationProfileRequest` đang là schema `{}`; không hiểu là endpoint không có field.
- Schema enum UserGender/UserRole đang mô tả integer trong khi JSON runtime cấu hình enum theo tên; gửi tên enum theo contract.
- `registrationToken` của register và `If-Match` của Avatar upload chưa đánh dấu required; BE vẫn yêu cầu chúng. GET trạng thái PayOS chưa mô tả response thành công đầy đủ.

Đây là các lỗi ghi nhận trên binary deployment ngày 03/10. Source auth ngày 05/10 đã sửa schema/metadata cho PATCH, multipart, enum, proof và header; xem [checklist auth](auth-api-checklist.md). Chưa kiểm OpenAPI sau khi deploy binary mới; Metadata GET PayOS và các API được chọn đã cập nhật source; xem [contract hiện tại](selected-api-contract.md). Deployment mới vẫn phải nghiệm thu riêng. [Kết quả Azure và cấu hình CORS](payos-deployment.md) phân biệt kiểm tra HTTP với nghiệm thu provider thật.

## Contract Building/IFC/scenario/release/support hiện tại

[Checklist/API inventory sinh từ OpenAPI](api-route-inventory.md), [phạm vi và bằng chứng](selected-api-contract.md), [release/access](release-building-access.md), [support/test tay](support-api.md). Các bằng chứng deployment tháng 09/03-10 phía trên là lịch sử, không mô tả binary mới.

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
| Building CRUD | Mutation kiểm actor/tenant DB, body organizationId cho admin create; query deprecated. Read scope DB; lifecycle/Building locks, atomic location/contact/audit. |

Ba role hiện có: `PlatformAdmin`, `OrganizationUser`, `Trainee`. Không có `OrganizationAdmin`. OrganizationUser phải có tổ chức hoạt động; hai role còn lại không có organizationId. Public register không cho chọn role/tổ chức. JWT được kiểm tra cả tài khoản, tổ chức và phiên DB; chưa tới exp vẫn có thể mất hiệu lực khi phiên bị thu hồi.

**Building CRUD:** create/update/archive kiểm actor/role/tenant từ DB và mutation/audit trong cùng transaction, khóa lifecycle/user. PlatformAdmin cần query `organizationId` đích cho mutation; OrganizationUser dùng tenant mình, không được đổi tenant. Body hiện chưa nhận tenant đích. Thiếu scope bị handler từ chối, không coi Guid.Empty là quyền admin. List/detail vẫn lọc organization claim (admin không có tenant có thể nhận list rỗng/404); không suy từ quyền mutation rằng read CRUD đã hỗ trợ admin toàn nền tảng.

### Lỗi và phân trang

Lỗi nghiệp vụ thường dùng ProblemDetails:

```json
{ "title": "Invalid email or password.", "status": 401, "code": "INVALID_CREDENTIALS" }
```

Framework có thể thêm type/traceId/errors. Một số legacy response có thể thiếu code; publish và review trả ProblemDetails kèm code, traceId. Middleware 401/403/429 có thể không có JSON. Ưu tiên HTTP status rồi mới đọc body nếu có.

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

Số điện thoại tổ chức unique sau normalization giữa mọi tổ chức, gồm inactive/soft-deleted; legacy NULL được giữ. Trim/bỏ khoảng trắng ASCII, `-`, `(`, `)`; giữ dấu `+` đầu và 6–15 chữ số. Không suy `0…` tương đương `+84…`; phone cá nhân độc lập. Khi index `organizations_phone_normalized_key` đã được triển khai, register organization và Google organization completion trả409 `ORGANIZATION_PHONE_EXISTS`/`errors.organizationPhoneNumber`; PATCH organization trả cùng code với `errors.phoneNumber`. Conflict rollback và giữ proof chưa hết hạn; PATCH không tăng revision/audit. Không thêm endpoint hoặc thay response thành công. [Preflight/deployment và test tay](organization-phone-manual-test.md).

Luồng và bằng chứng source chi tiết tại [authentication.md](authentication.md): cả Trainee và OrganizationUser dùng form → OTP → proof → register → login; JWT được cấp tại login/refresh, Google UID đã liên kết hoặc lần complete Google onboarding đầu tiên. Bảng dưới liệt kê API, không dùng số endpoint lịch sử để kết luận auth hoàn tất.

[Checklist 28 endpoint auth](auth-api-checklist.md) tách code/test/provider và có contract test đối chiếu OpenAPI. Swagger source hiện hiển thị đúng field PATCH/null semantics, multipart `file`, enum tên, registrationToken và header bắt buộc. Production Swagger chỉ đổi sau deploy binary này; không suy từ Markdown rằng deployment đã cập nhật. [Avatar test và signed S3 URL](avatar-manual-test.md).

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
| POST | `/api/me/link-google` | Live User + currentPassword + Google ID token | 200 GoogleLinkResponse + ETag; first link revokes sessions; 400/401/403/409/503 |
| POST | `/api/auth/google/onboarding/complete` | Public + onboarding proof | 201 Authenticated + authentication; replay409 ONBOARDING_ALREADY_COMPLETED;400 field/invalid/expired;409 input/identity conflict;503 retry |
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
- UID/email mới: trả `OnboardingRequired` kèm nested onboarding15 phút (email/displayName đã verified), chưa tạo account/session; complete tạo identity và session atomic, trả201 Authenticated.
- Email thuộc tài khoản khác/chưa liên kết UID này: 409 ACCOUNT_LINK_REQUIRED; không tự ghép chỉ vì trùng email.
- UID đã thay đổi trong lúc lấy khóa: 409 ACCOUNT_CHANGED; tài khoản/tổ chức bị khóa 403 ACCOUNT_DISABLED.
- Dưới khóa lifecycle/user, BE kiểm lại cả user ID sở hữu UID, role/tenant và pending legacy trước khi ghi session/audit. Account pending chưa verified/hết hạn vẫn trả403 EMAIL_NOT_VERIFIED/REGISTRATION_EXPIRED.

Google identity mới trả `{status:"OnboardingRequired", onboarding:{token:"<proof>",expiresAt:"<UTC>",email:"verified@example.test",displayName:null}}`, chưa tạo tài khoản. Root onboardingToken/expiresAt giữ cùng giá trị nhưng deprecated. Complete nhận proof + accountType canonical trainee/organization (alias Trainee/OrganizationUser), trả201 `{status:"Authenticated",authentication:TokenResponse}`. Không nhận client email/UID/tenant/password. Replay cùng input24 giờ trả409 ONBOARDING_ALREADY_COMPLETED; mất response/AlreadyCompleted thì exchange token Firebase hợp lệ, không phát lại JWT từ receipt. Khác input409 IDEMPOTENCY_KEY_CONFLICT. Proof sai/hết hạn400 ONBOARDING_TOKEN_INVALID/ONBOARDING_TOKEN_EXPIRED. Validation lỗi không consume proof; errors theo field. Quota429 và lock timeout503 có Retry-After. UID đã liên kết trả `{status:"Authenticated", authentication:TokenResponse}`. Chi tiết/test tay: [google-auth-manual-test.md](google-auth-manual-test.md).

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

TTL session theo cấu hình Jwt của môi trường; expiresAt trong nhánh onboarding là hạn proof, không phải TTL access token. `/api/me/link-google` yêu cầu live Bearer + currentPassword + Google idToken. Link mới revoke session/reset proofs, giữ email/role/tenant và trả200 GoogleLinkResponse yêu cầu login lại; same-UID replay với phiên mới không duplicate audit/revoke.

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

## 3. Accounts và Organizations

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

## 4. Buildings và Revision detail

| Method | Path | Quyền | Thành công |
| --- | --- | --- | --- |
| POST | `/api/buildings` | Building CRUD | 201 BuildingResponse |
| GET | `/api/buildings` | Building CRUD | 200 Page<BuildingSummaryResponse> |
| GET | `/api/buildings/{id}` | Building CRUD | 200 BuildingResponse |
| PUT | `/api/buildings/{id}` | Building CRUD | 200 BuildingResponse |
| DELETE | `/api/buildings/{id}` | Building CRUD | 200 BuildingSummaryResponse |
| POST | `/api/buildings/{id}/revisions/upload-url` | Editor | 201 InitiateIfcUploadResponse |
| GET | `/api/buildings/{id}/revisions` | Editor | 200 Page<RevisionResponse> |
| GET | `/api/buildings/{id}/trainings` | Trainee/OrganizationUser/PlatformAdmin | 200 TrainingDto[] |
| GET | `/api/revisions/{id}` | Editor | 200 RevisionResponse |

POST/PUT dùng các trường Building dưới đây; POST thêm `organizationId` tùy chọn trong body. PlatformAdmin bắt buộc chọn tổ chức đích; OrganizationUser mặc định dùng tenant lấy từ DB. PUT không phải partial PATCH:

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

Name bắt buộc tối đa 200 sau trim, totalFloors >= 1. buildingType/location/contact nullable. Nếu có contact, contactName trắng/null bị trả 400 trước Trim. Tọa độ nullable decimal; geojson là chuỗi, không phải object. Latitude [-90,90], longitude [-180,180], tối đa 8 chữ số thập phân. GeoJSON kiểm geometry/Feature, cấu trúc coordinates, tọa độ WGS84 hữu hạn và vòng polygon đóng. Contact email hợp lệ, tên tối đa 255, role 100, phone 50; buildingType 100, city/district 255. Lỗi trả `400 VALIDATION_ERROR` với `errors` theo field và traceId.

PUT với location/contact null giữ nested data hiện có, không xóa. Query `organizationId` của POST là alias deprecated; body/query khác nhau trả 400. PUT/DELETE admin lấy tenant từ Building, không cần gửi lại query. GET list admin có scope toàn nền tảng rõ ràng khi bỏ organizationId hoặc lọc query; OrganizationUser chỉ xem tenant của mình. Mutation khóa lifecycle trước Building, kiểm lại actor/organization dưới khóa, ghi Building/location/contact/audit trong cùng transaction. DELETE là archive; replay không ghi audit mới.

BuildingResponse: id, name, buildingType, totalFloors, isActive, organizationId, createdAt, updatedAt, location, contact. Nested response thêm id vào các trường request tương ứng. BuildingSummaryResponse: id, name, buildingType, totalFloors, isActive, createdAt.

List nhận page/pageSize/search/isActive. DELETE chỉ đặt isActive=false, **200 DTO, không 204**, không xóa vật lý. Chưa có route bật lại building.

Revision list nhận page/pageSize; detail nhận ID. Editor scope, Trainee 403, tài nguyên không thấy/ngoài phạm vi hoặc building archive có thể 404. RevisionResponse: id, buildingId, versionLabel, status, createdAt, sourceDocument nullable. SourceDocumentResponse: id, originalFilename, fileSizeBytes, quarantineStatus, createdAt; không object key/download URL.

Trainings trả mảng DTO. Trainee cần login, Building Public hoặc grant hiện hành; chỉ thấy Active Training trong lịch với Published release và approval đã pin. OrganizationUser chỉ tenant mình; admin scope rõ ràng. Không cấp seat/start/grant từ list. [Building access](release-building-access.md).

Upload-url là alias của `/api/buildings/{buildingId}/ifc`, dùng chung handler/receipt, request và response. Cùng actor/key/input trả cùng revision; không kéo dài TTL khi replay.

## 5. IFC commands

Tất cả cần Editor, rate limit administration. Storage phải được cấu hình thật; có route không đồng nghĩa worker IFC đã hoạt động.

| Method | Path | Input | Thành công |
| --- | --- | --- | --- |
| POST | `/api/buildings/{buildingId}/ifc` | InitiateIfcUploadRequest | 201 InitiateIfcUploadResponse |
| POST | `/api/revisions/{revisionId}/upload-complete` | FinalizeIfcUploadRequest | 204 |
| POST | `/api/revisions/{revisionId}/process` | Không body | 202 {jobId} |
| POST | `/api/processing-jobs/{jobId}/retry` | requestId, reason | 202 RetryProcessingJobResponse |
| POST | `/api/revisions/{revisionId}/confirm-for-training` | scenarioVersionId, validationRunId, annotationSetId | 200 {reviewId}; exact technical attestation |
| POST | `/api/revisions/{revisionId}/reviews` | scenarioVersionId, action, validationRunId, message | 201 reviewId |

### 5.1 Initiate → upload → finalize

Cả hai route initiate dùng chung receipt. Bắt buộc `Idempotency-Key` (1–128 ký tự, không whitespace/control), size dương trong giới hạn cấu hình, tên `.ifc`, versionLabel và SHA-256 từ file thật:

```json
{ "fileSizeBytes": 1048576, "originalFilename": "demo-building.ifc", "versionLabel": "v1", "sha256Hash": "<64 hexadecimal characters computed from the file>" }
```

Intent/audit được lưu trước khi ký URL; response `{revisionId, uploadUrl, objectKey}`. Cùng actor/key/input trả cùng revision kể cả đổi route; khác input trả 409. URL PUT tối đa 60 phút; replay không kéo dài TTL. Upload trực tiếp với `Content-Type: application/octet-stream`, không gửi JWT sang storage. Complete:

```json
{
  "objectKey": "<objectKey from initiate>",
  "fileSizeBytes": 1048576,
  "mimeType": "application/octet-stream",
  "sha256Hash": "<same SHA-256 as initiation>",
  "originalFilename": "demo-building.ifc"
}
```

BE kiểm owner/tenant/input trước S3. BE đọc có giới hạn, pin ETag, tính SHA-256 thật; ghi candidate riêng trước copy; kiểm bytes bản copy rồi finalize source/provenance/audit/cleanup atomic. Không giữ DB transaction khi chờ S3. Replay complete đã commit trả 204, không tạo source mới. Source legacy không được tự gắn nhãn verified.

Lỗi: 400 `VALIDATION_ERROR` theo field/key sai; 404 resource ngoài scope; 409 input conflict/source thay đổi; 410 intent hết hạn; 422 bytes/size/hash lệch; 503 chưa cấu hình, S3 unavailable hoặc attempt đang chạy (`Retry-After: 1`). TTL không làm mất receipt đã commit.

Mặc định upload tắt. Trước khi bật: áp `AddBoundIfcUploads`, đặt `IfcUpload:Enabled=true`, `IfcUpload:MaxBytes=<giới hạn deployment quyết định>`, `IfcUpload:CleanupEnabled=true` và S3. MaxBytes không có giá trị nghiệp vụ mặc định. Cleanup lease/retry giữ job đang được bảo vệ và tombstone đã xóa để tìm copy timeout hoàn thành muộn. Có thể tắt upload mới trong khi giữ cleanup bật.

Kiểm chứng PostgreSQL cô lập + storage giả lập; migration upload đã áp Supabase 07/10, S3/worker/binary thật chưa nghiệm thu. Xem [test IFC upload](ifc-upload-manual-test.md).

### 5.2 Process, retry, confirm

Process yêu cầu `Idempotency-Key`, chỉ nhận source đã verified. 202 `{jobId}`/Location xác nhận job/audit/outbox/receipt bền vững; chưa chứng minh worker đã chạy. Cùng input/key replay; nhiều key đồng thời không tạo nhiều logical job. Legacy source chưa verified trả 422. Dispatcher mặc định tắt, worker HTTP/machine gates xem [processing worker](processing-worker.md).

Retry job Failed:

```json
{
  "requestId": "44444444-4444-4444-8444-444444444444",
  "reason": "Retry after fixing worker configuration"
}
```

requestId là UUID khác Guid.Empty; reason không trống, tối đa 1000, được trim. Response gồm jobId và outcome `Requeued`/`AlreadyRequeued`, status 202 và Location job detail. Gửi lại cùng key/input không requeue lặp. Key cũ/input khác: 409 IDEMPOTENCY_CONFLICT. Key mới khi job không thể retry: 409 JOB_CONFLICT hoặc JOB_NOT_CLAIMABLE. Tạo key mới cho một lần retry chủ động mới.

Confirm tạo technical review đúng revision–scenarioVersion–validationRun–annotation/artifact đã accept, Passed và không Error/Critical blocker. Thành công 200 {reviewId}; mismatch/blocker 409. Không tự tạo release/training hoặc approval nội dung. Xem [readiness và approval](scenario-readiness.md).

## 6. IFC queries

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

## 7. Scenario, catalog, playtest and review

Tất cả cần Editor. Đây là luồng tác giả thiết kế/thử kịch bản, không phải phiên học và thống kê Trainee.

| Method | Path | Input | Thành công |
| --- | --- | --- | --- |
| POST | `/api/scenarios` | buildingId, name | 201 {id} |
| GET | `/api/buildings/{buildingId}/scenarios` | page, pageSize | 200 Page<ScenarioSummaryResponse> |
| GET | `/api/scenarios/{scenarioId}` | Không body | 200 ScenarioDetailResponse |
| POST | `/api/scenarios/{scenarioId}/draft` | revisionId | 201 {id} |
| GET | `/api/scenario-drafts/{draftId}` | Không body | 200 ScenarioDraftResponse + ETag |
| PUT | `/api/scenario-drafts/{draftId}` | State + If-Match; versioned `fet3d.editor/1` hoặc legacy | 204 + ETag; 422 `EDITOR_SCHEMA_INVALID`/`EDITOR_SCHEMA_VERSION_UNSUPPORTED`; 400 `EDITOR_JSON_MALFORMED` |
| POST | `/api/scenario-drafts/{draftId}/validate` | Không body | 200 ScenarioDraftValidationResponse |
| POST | `/api/scenario-drafts/{draftId}/snapshot` | Không body | 201 {id}; draft versioned không hợp lệ 422 `EDITOR_SCHEMA_INVALID` |
| GET | `/api/scenarios/{scenarioId}/versions` | page, pageSize | 200 Page<ScenarioVersionSummaryResponse> |
| GET | `/api/scenario-versions/{versionId}` | Không body | 200 ScenarioVersionDetailResponse |
| GET | `/api/scenario-interactions/catalog` | Không body | 200 RuntimeCatalogDto[] kèm `capabilityContracts` hợp lệ |
| POST | `/api/scenarios/{scenarioId}/playtests` | Optional buildingId; exact draft/version + Idempotency-Key; OrganizationUser | 201 PlaytestPreparation |
| POST | `/api/playtests/{playtestId}/start` | runtimeVersion + Idempotency-Key; OrganizationUser launching session | 200 PlaytestLaunch with 5-minute grant, status Launching |
| GET | `/api/playtests/{playtestId}` | Owner | 200 PlaytestStatusResponse |
| POST | `/api/playtests/{playtestId}/handoffs` | Idempotency-Key; owner, Created | 201 PlaytestHandoffResponse (5-minute single-use code) |
| POST | `/api/playtests/handoffs/redeem` | code; another session of the same owner | 200 PlaytestStatusResponse |
| POST | `/api/playtests/{playtestId}/launch-grants` | Idempotency-Key; launching session, Launching | 200 PlaytestLaunch (next generation) |
| POST | `/api/playtests/{playtestId}/launched` | generation | 200 PlaytestStatusResponse (Running) |
| POST | `/api/playtests/{playtestId}/heartbeat` | No body | 200 PlaytestHeartbeatResponse |
| POST | `/api/playtests/{playtestId}/events:batch` | events[] | 200 PlaytestEventsResponse |
| POST | `/api/playtests/{playtestId}/complete` | lastEventSequence, summary + Idempotency-Key | 200 PlaytestStatusResponse (Completed) |
| POST | `/api/playtests/{playtestId}/cancel` | Idempotency-Key | 200 PlaytestStatusResponse (Cancelled) |
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

Body trên minh họa cấu trúc cơ bản, chưa đủ rubric/learningObjectives/learnerInstructions cho snapshot v7. Dùng [scenario-authoring.md](scenario-authoring.md) để lấy request đầy đủ; structural validation kiểm anchor/capability/catalog/reference nhưng không chứng minh geometry QA hay Unity thật.

- If-Match: thiếu428, malformed400, stale412; dùng ETag từ GET.
- Snapshot cũng yêu cầu If-Match và Idempotency-Key. Create scenario/draft yêu cầu Idempotency-Key; numbering được khóa và DB unique constraint bảo vệ.
- Thành công: 204 với `ETag: "<newVersion>"`; giữ version mới cho lần lưu tiếp.

Version dựa trên PostgreSQL xmin, **không mặc định là 1**. GET draft trả state và `ETag: "<version>"`; dùng ETag đó cho lần PUT tiếp theo.

Snapshot không body, trả 201 id ScenarioVersion. Đây là snapshot state, không publish hoặc tự tạo runtime package.

`POST /api/scenario-drafts/{draftId}/validate` trả `{draftId, version, isValid, issues}`. Mỗi issue có `code`, đường dẫn JSON `path`, và `message`. Hiện endpoint đồng bộ kiểm tra cấu trúc draft: spawn point, hazard/position, scoring và evacuation route. Nó kiểm runtime reference/capability catalog và IFC anchors từ dữ liệu đã accept, nhưng không chứng nhận geometry QA/Unity thật; response hợp lệ không phải confirmation-for-training.

`POST /api/revisions/{revisionId}/reviews` chỉ tạo review `Rejected` cho một cặp revision–scenario version. Body phải đưa validation run cùng cặp đó và lý do 1–4000 ký tự; annotation set là tùy chọn nhưng nếu có phải thuộc revision. Server ghi review và audit trong một transaction, không chuyển trạng thái chung của revision sang Rejected. Version đã có review trả 409 `CONFLICT`.

### 7.3 Catalog/playtest

Catalog trả mảng `{runtimeVersion, protocolVersion, manifestSchemaVersion, capabilities}`; capabilities là JSON. Dùng giá trị catalog/package thực tế khi tích hợp runtime.

Playtest prepare/start use the session-bound PostgreSQL gate: exact accepted immutable PlaytestPackage, live OrganizationUser owner/tenant, runtime catalog compatibility and Building entitlement. Preparation assigns no entitlement and consumes no Trial; start does (once) and enters `Launching`; the runtime confirms `Running` with the current grant generation. Mobile handoff, grant reissue, heartbeat, telemetry, complete and cancel with error codes: [requests, configuration and manual tests](playtest-manual-test.md). The legacy unrestricted store is not registered and refuses mutations without session proof.

## 8. Release lifecycle và Building access

POST /api/releases: OrganizationUser/PlatformAdmin, Idempotency-Key, revisionId/scenarioVersionId/confirmationReviewId/candidateArtifactId; metadata legacy nullable chỉ được nhận khi khớp output worker. Gate kiểm Approved content/rubric và technical confirmation đúng cặp, current accepted ReleasePackage/manifest và runtime contract; tạo Built/package/provenance/Training/receipt/audit atomic. Replay cùng key/input trả cùng response201; khác input409. Không chứng minh Unity thật.

GET /api/releases/{releaseId} trả ReleaseResponse; POST /revoke nhận reason và trả204, atomic/replay không thêm audit. POST /publish có gate entitlement/approval/readiness/provenance, bật bằng Publishing:Enabled;bắt buộc Idempotency-Key,200 ReleaseResponse/replay cùng key/input;409 khi thiếu gate hoặc key conflict,503 khi rollout chưa bật. GET trả trạng thái hiện tại; replay receipt không republish release đã Revoked.

GET/PATCH /api/buildings/{id}/access, POST /participation-code/rotate, DELETE /participation-code dùng tenant/role server và If-Match cho mutation. POST /participation/verify chỉ Trainee, grant gắn account/revision. Xem [contract/test release–access](release-building-access.md).

## 8.1 Feedback/support và PayOS metadata

[Support](support-api.md): create/message Idempotency-Key; admin PATCH If-Match; detail ETag; list và message history phân trang20/max100. User chỉ tài nguyên do mình tạo; admin kiểm live role; mutation/receipt/audit atomic.

GET /api/payments/payos/checkouts/{id} khai báo200 PayosCheckoutResponse; GET /requests/{id} khai báo200 PayosPaymentResponse. OrganizationUser cùng tenant hoặc PlatformAdmin. Checkout, payment và provisioning riêng: Paid không đồng nghĩa mọi dòng đã được provision. Nghiệp vụ PayOS không đổi.

## 9. Giới hạn thấy khi đối chiếu source

| Phần | Hiện trạng và ảnh hưởng |
| --- | --- |
| Building CRUD | Mutation kiểm actor/tenant DB, body organizationId cho admin create; query deprecated. Read scope DB; lifecycle/Building locks, atomic location/contact/audit. |
| Upload-url cũ | Đã là alias tương thích của initiation IFC; client mới dùng `/api/buildings/{buildingId}/ifc` |
| IFC finalize | Bound intent/source, bounded stream/ETag/hash thật và cleanup recovery; fake S3/PostgreSQL tested. Upload migration đã áp, S3/binary thật chưa nghiệm thu. |
| IFC process | Verified source; canonical schema1 outbox/hash/tenant/job/audit/receipt atomic. Leased HTTP dispatcher/machine worker gates/retry/fencing đã test giả lập; IFC/Unity thật chưa nghiệm thu. |
| Draft editor | GET draft state/version đã có. Kiểm tra response ETag/xmin trước khi tích hợp; không coi đây là API còn thiếu. |
| Playtest prepare | Pins exact accepted immutable package/version/run without Trial consumption or grant; requires OrganizationUser live session and Idempotency-Key. |
| Playtest start | Live session/owner/tenant/runtime/Building entitlement gate, atomic Trial and receipt/audit; dedicated 5-minute grant. Real Unity integration remains unverified. |
| Release/training | Built checks approval/readiness/current ReleasePackage, derives package, creates Training atomic. Participation/access and authorized listing implemented. Publish dùng gate paid entitlement/approval/readiness/package và cờ rollout (200 ReleaseResponse khi đạt; Idempotency-Key bắt buộc;409 thiếu điều kiện/conflict;503 khi tắt); learner start/sync/result chưa có. |
| Auth | Form → OTP → proof → register → login cho Trainee/OrganizationUser; OrganizationUser route chưa nhận username cá nhân. Request/verify OTP không tạo identity; verify-email link chỉ cho pending legacy. Profile cá nhân/tổ chức và avatar mutation dùng ETag; logout-all đã có. AvatarService reserve candidate trước conditional S3 copy, có cleanup/recovery source; provider/deployment cần kiểm riêng. Google onboarding completion và explicit link đã có. Xem [Google contract/test tay](google-auth-manual-test.md); Firebase/client/deployment chưa kiểm chứng. |
| Token response | Login local, Firebase login và refresh đều không trả expiresAt |
| Device | Installation proof và family session được kiểm khi bind/revoke; FCM send chỉ dùng binding active. Không coi test mock là bằng chứng FCM production delivery. |

Số endpoint không phản ánh mức độ hoàn thiện luồng. Cập nhật tài liệu không thay source, chạy migration hoặc xác nhận kết nối dịch vụ thực tế.

### Khoảng cách tới contract v7

| Capability đích | Việc cần triển khai trong checklist |
| --- | --- |
| Organization Library / Admin approval | LIBRARY-01, APPROVAL-01: template/rubric/thiết bị version hóa; duyệt scenario/rubric đúng hash, tách readiness kỹ thuật. |
| Private Building | Access/participation/list có source/test; migration/binary/client còn phải triển khai. Learner start gate và canonical QR vẫn backlog. |
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

1. Firebase Google → JSON string ID token: UID đã link trả Authenticated; UID mới trả OnboardingRequired + proof15 phút, chưa tạo account. Complete chọn trainee/organization, trả201 authentication; cùng input24 giờ trả409 AlreadyCompleted, exchange lại để phục hồi nếu mất response. Explicit `/api/me/link-google` yêu cầu Bearer/local password + Google token, giữ role/tenant và revoke phiên/reset proof khi link mới. Firebase email/password provider không dùng được ở exchange/link này.
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
| GET | `/api/revisions/{revisionId}/floors` | Không body | 200 `RevisionFloorsResponse` |
| GET | `/api/revisions/{revisionId}/annotations` | Không có body | 200 snapshot + header `ETag` |
| PUT | `/api/revisions/{revisionId}/annotations` | Body bên dưới; header `If-Match` lấy từ GET | 200 snapshot mới + `ETag` mới |

Preview trả `buildingId`, `revisionId`, `revisionStatus`, `status`, `artifactId`, `attemptId`, `sha256Hash`, `downloadUrl`, `expiresAt`, `coordinateTransform`, `floors`, `semanticMapping`, `schemaVersion`, `units`, `upAxis`, `handedness`. Không trả storage key. Chỉ xét artifact `preview_glb` thuộc current attempt của Geometry job, job/attempt Succeeded và input hash khớp.

- `Ready`: metadata đạt contract [`fet3d.editor/1`](../contracts/editor/v1/README.md), hash SHA-256 hợp lệ, S3 HEAD xác nhận object và kích thước; trả signed GET URL 5 phút cùng tọa độ.
- `NotReady`: chưa có artifact được chấp nhận, hoặc object chưa xác nhận được; URL/expiry null.
- `ReprocessRequired`: có artifact nhưng metadata là legacy (không có `schemaVersion`) hoặc không đạt contract. Không ký URL và không trả `coordinateTransform`/`floors`/`semanticMapping`, để không phát tọa độ giả; cần chạy lại Geometry bằng worker xuất contract mới.

Contract tọa độ: GLB đơn vị mét, Y-up, hệ tay phải; ma trận là 16 số hữu hạn, column-major, nhân column vector (`x' = m0·x + m4·y + m8·z + m12`), hàng 4 là `0,0,0,1` và phải khả nghịch. `coordinateTransform` là IFC→GLB, đã gồm đổi đơn vị và origin; `floors[].transform` là tọa độ cục bộ tầng→GLB. `semanticMapping` là mảng `{ifcGlobalId,nodeId,floorId,semanticType}`. JSON Schema, fixture và điểm mẫu nằm trong [contracts/editor/v1](../contracts/editor/v1/README.md).

Worker gửi metadata Geometry có `schemaVersion` phải đạt schema trước khi output được nhận: sai trả `422 EDITOR_SCHEMA_INVALID` kèm `issues`, version lạ trả `422 EDITOR_SCHEMA_VERSION_UNSUPPORTED`. Metadata không có `schemaVersion` vẫn được nhận như legacy nhưng luôn bị đọc là `ReprocessRequired`. Sai/missing GUID đầu vào trả 400. Trạng thái Ready không cấp quyền publish hoặc training, và không phải nghiệm thu geometry QA hay Unity.

`GET /api/revisions/{revisionId}/floors` dùng cùng artifact và cùng phân loại: `Ready` trả `floors` (ID ổn định trong revision, tên, `elevationMeters`, `ifcGlobalId` nếu có, `transform`), `NotReady`/`ReprocessRequired` trả `floors: null`. Không cần S3.

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


Authoring hardening: draft PUT/snapshot require quoted xmin If-Match (428/400/412); scenario/draft/snapshot/package-build use durable idempotency receipts. V7 snapshots require explicit rubric/learner fields; structural validation does not establish readiness. See [scenario authoring](scenario-authoring.md). Isolated PostgreSQL/HTTP/fake package tests ran; real Unity and deployment remain unverified.

Readiness/content approval: [contract and manual tests](scenario-readiness.md). Submit/approve/reject use Idempotency-Key and exact immutable content/rubric hashes; technical readiness remains separate.

Selected Task 6 source/HTTP/isolated PostgreSQL/runtime-fake evidence: [playtest-manual-test.md](playtest-manual-test.md). No learner plays or publish completion is inferred.


## Billing v7 / publish / reporting rollout (source branch)

### Publish and scenario review read contracts

`POST /api/releases/{releaseId}/publish` requires Idempotency-Key and returns 200 ReleaseResponse when Publishing is enabled and every gate passes. Same actor/key/input replays the original response; changed canonical input returns409 IDEMPOTENCY_KEY_CONFLICT. GET reads current release state; replay after revoke never republishes. Missing QA, blockers, package/runtime, approval, readiness, entitlement or active Training has a distinct409 code. The disabled rollout flag remains503. [Errors, migration order and manual acceptance](publish-review-manual-test.md).

| Method | Route | Response / scope |
|---|---|---|
| GET | `/api/admin/scenario-reviews` | 200 PageResponse<ScenarioReviewSummary>; PlatformAdmin; status/organizationId/page/pageSize |
| GET | `/api/admin/scenario-reviews/{reviewId}` | 200 ScenarioReviewDetail; PlatformAdmin |
| GET | `/api/scenario-versions/{versionId}/review` | 200 ScenarioReviewDetail; own-tenant OrganizationUser or PlatformAdmin |

Review detail has nested `review` metadata (hashes, names, submitter/decision/reason), frozen `content`/`rubric`/objectives/instructions and `readiness`. Scenario version detail/list add reviewStatus/reviewId/rejectReason. Queue defaults20/max100, stable submittedAt/ID descending. Invalid filters400, foreign tenant404, revoked family401; no direct review-table SELECT for runtime. [Readiness and review contract](scenario-readiness.md).

Upgrade (`purchaseAction: Upgrade`, one-time Admin price, same period/seats), AI top-up (`purpose: AIQuotaTopUp`), `GET /api/buildings/{id}/service-entitlement`, `GET /api/organizations/me/ai-quota[/grants]`, `GET /api/organizations/me/ai-usage`, the Admin organization equivalents, enterprise detail/status/quotation and expiry reminders are documented with error codes in the [rollout notes](billing-v7-rollout.md).

See [current contract and migrations](billing-v7-rollout.md), [safe reporting DTOs](reporting-operations.md), and [Swagger manual acceptance](publish-billing-v7-manual-test.md). Package6/12-month pricing stays monthly; quotation pins service periods/capacity/quota before Issue. Publish uses paid approval/readiness/package gates when enabled; operational metrics are not learner/revenue analytics. Source/Docker tests do not establish shared DB/provider deployment.
# Selected worker/API contract update

Selected mutations recheck the JWT session family under lifecycle/actor/resource locks, including readiness, content approval and catalog/discount/quota-policy changes. The client never supplies the family in JSON. Revoked/expired family returns401 before receipt replay. Annotations PUT requires quoted If-Match (428/400/412), appends a version atomically and validates anchors against accepted current Geometry provenance; GET/PUT declare ETag. Preview HEAD checks size before URL signing; missing/size mismatch is NotReady, provider failure503 PREVIEW_STORAGE_UNAVAILABLE. New readiness/approval audits expose only safe allowlisted changes. See [manual checks](api-worker-contract-manual-test.md) and [worker SQL permissions](worker-permissions.md) for schema-first rollout and deployment evidence boundaries.
