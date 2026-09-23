# Fire3D — Hướng dẫn tích hợp API hiện tại

Cập nhật **23/09/2026**, bổ sung editor preview và annotations trên nhánh `fix/auth-registration`; các mục trước giữ nội dung đối chiếu ngày 22/09.

Source hiện có **61 operation controller** (đếm HTTP action), trong đó một endpoint trả `501`. Số lượng route không xác nhận các luồng đã chạy end-to-end hay toàn bộ API trong proposal. Các phần thiếu được ghi rõ ở mục 9.

## 1. Quy ước tích hợp

- Dùng origin BE đang chạy làm `BASE_URL`; đường dẫn bên dưới đã có `/api`.
- Swagger `/swagger`, OpenAPI `/openapi/v1.json`, health `/health` không tính vào 61 operation. Health không chứng minh DB/Mailgun/Firebase/storage đã kết nối thành công.
- Body: `Content-Type: application/json`, tên thuộc tính `camelCase`, GUID là chuỗi UUID, ngày giờ ISO 8601.
- Enum JSON dùng tên như `"OrganizationUser"`; không gửi số cho role.
- Không có envelope chung `{success,data}`. Đọc trực tiếp DTO. `204` và một số `200` không có body; không luôn gọi `response.json()`.
- POST tạo resource đồng bộ trả `201 Created` cùng header `Location`. POST yêu cầu xử lý nền bền vững trả `202 Accepted`; POST action trên resource đã có có thể trả `200` hoặc `204`.
- Các ID, token, hash, password minh họa phải thay bằng dữ liệu test thực tế.

### POST status và Location

| Path | Thành công | Header `Location` |
| --- | --- | --- |
| `POST /api/auth/register` | 201 AccountResponse | `/api/accounts/{id}` |
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

**Building CRUD chưa thống nhất với Editor:** tài khoản không có tổ chức được truyền Guid.Empty, store lọc đúng bằng organization ID. Không giả định PlatformAdmin thao tác xuyên tổ chức ở CRUD building: list có thể rỗng, detail 404, create có thể lỗi DB. Body chưa nhận tenant đích. Đây là thiếu sót implementation, không phải cách cấp quyền cho frontend.

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
| 501 | Endpoint upload-url cũ chưa triển khai |

Policy auth: **10 request/phút/IP** cho sáu auth public endpoint và POST accounts. Policy administration: **120 request/phút/IP** cho administration và IFC commands; POST accounts dùng auth. Fixed window, không xếp hàng; không suy ra tất cả route đều dùng hai policy này.

Phân trang mặc định page=1, pageSize=20; page 1..100000, pageSize 1..100. Search quản trị/building tối đa 200 ký tự. `Page<T>` bên dưới có dạng:

```json
{ "items": [], "totalCount": 0, "page": 1, "pageSize": 20 }
```

## 2. Authentication - 12 endpoint

| Method | Path | Quyền | Thành công |
| --- | --- | --- | --- |
| POST | `/api/auth/register` | Public | 201 AccountResponse |
| POST | `/api/auth/login` | Public | 200 LoginResponse |
| POST | `/api/auth/login-firebase` | Public | 200 TokenResponse |
| POST | `/api/auth/refresh` | Public | 200 TokenResponse |
| POST | `/api/auth/logout` | User | 204 |
| GET | `/api/auth/me` | User | 200 AccountResponse |
| PATCH | `/api/auth/me` | User | 200 AccountResponse |
| PUT | `/api/auth/devices` | User | 200 rỗng |
| DELETE | `/api/auth/devices/{deviceUuid}` | User | 204 |
| POST | `/api/auth/forgot-password` | Public | 202 với message chung |
| POST | `/api/auth/reset-password` | Public | 204 |
| POST | `/api/auth/change-password` | User | 204 |

### 2.1 Register local

```json
{
  "email": "trainee@example.com",
  "password": "Example-Password-2026!",
  "fullName": "Nguyen Van A"
}
```

Email hợp lệ tối đa 254 ký tự, trim/lowercase; password 12–128 và không chỉ khoảng trắng; fullName bắt buộc, không chỉ khoảng trắng, tối đa 200. Không gửi role/organizationId để cấp quyền.

BE hash password vào `users.password_hash`, không tạo tài khoản email/password trên Firebase. Trả **201 AccountResponse**, chưa đăng nhập; gọi login tiếp theo:

```json
{
  "id": "11111111-1111-4111-8111-111111111111",
  "email": "trainee@example.com",
  "fullName": "Nguyen Van A",
  "role": "Trainee",
  "organizationId": null
}
```

Lỗi: 400 VALIDATION_ERROR, 409 EMAIL_EXISTS. AccountResponse từ nguồn tạo khác có thể có fullName null.

### 2.2 Login local

```json
{ "email": "trainee@example.com", "password": "Example-Password-2026!" }
```

Email được chuẩn hóa; password login không rỗng, tối đa 128, không áp lại minimum 12 như lúc tạo/reset. Response:

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

BE kiểm token, trạng thái thu hồi, email đã xác minh và provider google.com. Input rỗng/quá 16384 ký tự: 400; token/provider sai: 401 INVALID_FIREBASE_TOKEN.

- UID đã liên kết: dùng hồ sơ/role DB.
- UID/email mới: tạo Trainee, organizationId null, password hash local null.
- Email thuộc tài khoản khác/chưa liên kết UID này: 409 ACCOUNT_LINK_REQUIRED; không tự ghép chỉ vì trùng email.
- Xung đột tạo đồng thời có thể 409 ACCOUNT_EXISTS; tài khoản bị khóa 403 ACCOUNT_DISABLED.

Response hiện là TokenResponse, **vẫn có expiresAt**:

```json
{
  "accessToken": "<Fire3D JWT>",
  "accessTokenExpiresAt": "2026-09-22T10:00:00Z",
  "refreshToken": "<refresh token>",
  "refreshTokenExpiresAt": "2026-09-29T09:00:00Z",
  "user": {
    "id": "11111111-1111-4111-8111-111111111111",
    "email": "trainee@example.com",
    "fullName": "Nguyen Van A",
    "role": "Trainee",
    "organizationId": null
  }
}
```

Thời gian ví dụ không thay cấu hình môi trường. Chưa có API liên kết Google vào tài khoản local có sẵn.

### 2.4 Refresh, logout, me, devices

Refresh không cần access token:

```json
{ "refreshToken": "<latest refresh token>" }
```

Token không trống, tối đa 256. Thành công trả TokenResponse còn hai expiresAt. Refresh luân chuyển token; lưu cả cặp mới, tránh nhiều request refresh đồng thời. Không kéo dài thời hạn tuyệt đối của family. Token không hợp lệ trả 401 INVALID_REFRESH_TOKEN; replay token đã dùng có thể thu hồi cả family.

Logout requires Bearer, returns 204 and revokes the current session family only. Me returns AccountResponse or 401. PATCH `/api/auth/me` updates only the current user full name; role, organization, email and status are immutable. DELETE `/api/auth/devices/{deviceUuid}` requires Bearer, is idempotent and returns 204.

Devices upsert cho user hiện tại:

```json
{
  "deviceUuid": "test-device-001",
  "fcmToken": null,
  "deviceModel": "Test device",
  "osVersion": "Windows 11"
}
```

The controller overwrites the command UserId with the JWT subject; the client does not send it. `deviceUuid` is a 1-255 character string and does not have to be a GUID; FCM token is at most 255 characters, deviceModel at most 200 and osVersion at most 100. Invalid input returns 400. PUT upsert returns an empty 200; DELETE with the same deviceUuid returns 204 and revokes only push delivery for the current user.

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

Phải dùng token thật từ email: 64 ký tự hex, hạn 30 phút, một lần; DB chỉ lưu hash token. Password 12–128, không chỉ khoảng trắng. Sai/hết hạn/đã dùng token: 400 INVALID_RESET_CODE; password sai: 400 INVALID_PASSWORD; thành công 204.

Reset changes the local hash, consumes the reset token, invalidates remaining reset tokens and revokes Fire3D sessions in one transaction. The client must sign in again. A Google-only account without `password_hash` cannot use reset to create a local password; set-password/link Google is a separate API. Firebase passwords and oobCode are not changed or accepted.

Chi tiết vận hành: [password-reset.md](password-reset.md).

### 2.6 Change password khi đã đăng nhập

Endpoint cần `Authorization: Bearer <Fire3D accessToken>` và không nhận `actorId` từ body:

```json
{
  "currentPassword": "Current-Example-Password-2026!",
  "newPassword": "New-Example-Password-2026!"
}
```

`currentPassword` không rỗng và tối đa 128 ký tự. `newPassword` phải dài 12–128 ký tự, không chỉ khoảng trắng và phải khác mật khẩu hiện tại. Sai mật khẩu hiện tại: 400 `INVALID_CURRENT_PASSWORD`; mật khẩu mới không hợp lệ: 400 `INVALID_PASSWORD`; trùng mật khẩu hiện tại: 400 `PASSWORD_UNCHANGED`; tài khoản/organization không còn hoạt động: 401 `UNAUTHORIZED`.

Thành công trả 204. Trong cùng transaction khóa theo user, BE cập nhật `password_hash`, vô hiệu token reset chưa dùng, thu hồi toàn bộ refresh session và ghi audit. Access JWT hiện tại sẽ không còn được chấp nhận sau khi family session bị thu hồi; client phải đăng nhập lại. Endpoint không gửi email, không thay đổi password Google/Firebase và không nhận Firebase oobCode.

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

Email tối đa 254 hợp lệ; password 12–128 không chỉ khoảng trắng; fullName tùy chọn tối đa 200; role bắt buộc. OrganizationUser phải có organizationId hoạt động; role khác phải null/không gửi. Admin tạo cũng hash password local. Lỗi 400 VALIDATION_ERROR/INVALID_ORGANIZATION, 409 EMAIL_EXISTS, 403 FORBIDDEN.

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
| POST | `/api/buildings/{id}/revisions/upload-url` | User | **501, chưa triển khai** |
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

Name bắt buộc tối đa 200 sau trim, totalFloors >= 1. buildingType/location/contact nullable. Nếu có contact phải có contactName: handler Trim trực tiếp. Tọa độ nullable decimal; geojson là chuỗi, không phải object. Chưa validate đầy đủ tọa độ/GeoJSON/contact; không giả định mọi DB exception đều chuyển thành 400.

PUT với location/contact null giữ nested data hiện có, không xóa. OrganizationId lấy từ JWT, không có trong request.

BuildingResponse: id, name, buildingType, totalFloors, isActive, organizationId, createdAt, updatedAt, location, contact. Nested response thêm id vào các trường request tương ứng. BuildingSummaryResponse: id, name, buildingType, totalFloors, isActive, createdAt.

List nhận page/pageSize/search/isActive. DELETE chỉ đặt isActive=false, **200 DTO, không 204**, không xóa vật lý. Chưa có route bật lại building.

Revision list nhận page/pageSize; detail nhận ID. Editor scope, Trainee 403, tài nguyên không thấy/ngoài phạm vi hoặc building archive có thể 404. RevisionResponse: id, buildingId, versionLabel, status, createdAt, sourceDocument nullable. SourceDocumentResponse: id, originalFilename, fileSizeBytes, quarantineStatus, createdAt; không object key/download URL.

Trainings trả mảng, không phân trang: id, releaseId, name, description nullable, status, startDate/endDate nullable, allowedModes (string[]), createdAt. Query lọc training Active và release Published, chưa lọc khoảng ngày. Đây là Editor API, không phải danh sách học public/Trainee.

Upload-url cũ luôn 501; dùng initiation IFC bên dưới.

## 5. IFC commands — 5 endpoint

Tất cả cần Editor, rate limit administration. Storage phải được cấu hình thật; có route không đồng nghĩa worker IFC đã hoạt động.

| Method | Path | Input | Thành công |
| --- | --- | --- | --- |
| POST | `/api/buildings/{buildingId}/ifc` | InitiateIfcUploadRequest | 200 InitiateIfcUploadResponse |
| POST | `/api/revisions/{revisionId}/upload-complete` | FinalizeIfcUploadRequest | 204 |
| POST | `/api/revisions/{revisionId}/process` | Không body | 202 {jobId} |
| POST | `/api/processing-jobs/{jobId}/retry` | requestId, reason | 202 RetryProcessingJobResponse |
| POST | `/api/revisions/{revisionId}/confirm-for-training` | Không body | 200 rỗng |

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

## 6. IFC queries — 7 endpoint

Tất cả cần Editor; list nhận page/pageSize. Sai filter/Guid.Empty 400, sai role 403, không thấy/ngoài tổ chức 404.

| Method | Path | Thành công |
| --- | --- | --- |
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

Contract thành công 201 id playtest. Start không body, chuyển Created → Running; khác Created 400 INVALID_STATE; không thấy/ngoài phạm vi 404. Không trả launch token, manifest hay URL package. Prepare/start còn rủi ro DB/entitlement (mục 9); 200 không chứng minh Unity đã khởi chạy hoặc đã ghi kết quả huấn luyện.

## 8. Publish release — 1 endpoint

| Method | Path | Quyền | Input | Thành công |
| --- | --- | --- | --- | --- |
| POST | `/api/releases/{releaseId}/publish` | Editor | Không body | 200 rỗng |

Release trong phạm vi phải Built, chuyển Published, ghi publishedAt/publishedBy/audit. Sai trạng thái 400 INVALID_STATE, không thấy/ngoài phạm vi 404; controller không thêm code vào ProblemDetails.

Chưa có API tạo/build release. Store publish chỉ kiểm Built, không bảo đảm đã kiểm đầy đủ QA/entitlement/package. Không tự tạo training; GET trainings chỉ trả training Active gắn release Published.

## 9. Giới hạn thấy khi đối chiếu source

| Phần | Hiện trạng và ảnh hưởng |
| --- | --- |
| Building CRUD | Scope khác Editor, PlatformAdmin không chọn tenant đích, role guard chưa nhất quán |
| Upload-url cũ | Luôn 501; dùng initiation IFC |
| IFC finalize | Chưa ràng buộc đủ key với revision/upload; chưa kiểm hash nội dung; validation MIME/tên/hash hạn chế |
| IFC process | Outbox còn literal payload_hash = 'hash', không phải SHA-256 hợp lệ; chưa bảo đảm tương thích schema/gate/worker. Process/confirm truy cập navigation Building nhưng query không Include Building, có nguy cơ null |
| Draft editor | Thiếu GET state/version hoặc version trong create response; chặn việc lấy If-Match đầu tiên |
| Playtest prepare | SQL entitlement dùng is_active, bắt exception rồi giữ Guid.Empty; cần đối chiếu schema. Draft-only có thể ghi ScenarioVersionId = Guid.Empty và vướng FK |
| Playtest start | Mới đổi trạng thái/audit, chưa trả launch grant |
| Release/training | Thiếu create/build release và vòng đời training/session; publish chưa bảo đảm đầy đủ QA gates |
| Auth | Local email verification, Google link/unlink and logout-all are not implemented; duplicate email is never implicitly linked. Google onboarding and username requirements remain implementation work. |
| Token response | Login local bỏ expiresAt, Firebase login/refresh vẫn còn |
| Device | Device identifiers and FCM metadata are validated; PUT upsert returns 200 and DELETE revoke returns 204. Delivery is not guaranteed by the API response. |

Số endpoint không phản ánh mức độ hoàn thiện luồng. Cập nhật tài liệu không thay source, chạy migration hoặc xác nhận kết nối dịch vụ thực tế.

## 10. Checklist tích hợp và test tay

### Auth local

1. Register email mới → 201 Trainee, organizationId null, không token; trùng → 409.
2. Login sai → 401; đúng → 200 LoginResponse không expiresAt.
3. Me không Bearer → 401; Fire3D JWT hợp lệ → đúng tài khoản.
4. Refresh → lưu cặp mới. Test replay riêng vì token cũ có thể thu hồi family.
5. Logout → 204; phiên vừa logout không gọi API bảo vệ được.
6. Forgot email hợp lệ có/không tồn tại → cùng 202. Xác minh worker/Mailgun riêng.
7. Reset token thật → 204; password cũ không login được, password mới được; token dùng lại → 400; phiên cũ bị thu hồi.

### Google, admin và tenant

1. Firebase Google → JSON string ID token → 200 TokenResponse; Firebase email/password provider không dùng được ở route này.
2. Local account trùng email Google chưa liên kết → 409, không tự ghép/nâng quyền.
3. Admin tạo organization → 201; tạo OrganizationUser → 201; login local tài khoản vừa tạo.
4. Trainee gọi Editor → 403; OrganizationUser đọc revision/job tổ chức khác → 404; kiểm PlatformAdmin riêng cho Editor.
5. Admin tự deactivate → 409; deactivate organization → OrganizationUser mất điều kiện truy cập.

### IFC và authoring

1. Chuẩn bị OrganizationUser, building, storage; initiate → upload bytes → finalize. Sai object/file → lỗi tương ứng.
2. Sửa blocker process/worker rồi mới test chuỗi process → poll → QA/artifacts/BIM facts; không dùng QA lịch sử làm kết luận hiện tại.
3. Retry Failed cùng requestId → không requeue lặp; key cũ/reason khác → 409.
4. Bổ sung contract version ban đầu rồi test create → edit; thiếu header 412, version cũ 409, lưu đúng 204/ETag.
5. Test playtest/package/entitlement/publish với DB/runtime thật sau khi hoàn thiện; không đồng nhất playtest và Trainee session.

## 11. Nguồn đối chiếu và bảo trì

- Routes/status/quyền controller: [Controllers](../Fire3D/Fire3D.API/Controllers).
- Auth DTO/handler: [Authentication](../Fire3D/Fire3D.Application/Authentication).
- Quản trị: [AdministrationContracts.cs](../Fire3D/Fire3D.Application/Administration/AdministrationContracts.cs).
- Building/revision: [BuildingContracts.cs](../Fire3D/Fire3D.Application/Buildings/BuildingContracts.cs).
- IFC: [Application/Ifc](../Fire3D/Fire3D.Application/Ifc), [IfcWriteStore.cs](../Fire3D/Fire3D.Infrastructure/Ifc/IfcWriteStore.cs).
- Draft: [ScenarioDraftStateDto.cs](../Fire3D/Fire3D.Application/Scenarios/Dto/ScenarioDraftStateDto.cs).
- Playtest: [PlaytestWriteStore.cs](../Fire3D/Fire3D.Infrastructure/Scenarios/PlaytestWriteStore.cs).
- Publish: [ReleaseWriteStore.cs](../Fire3D/Fire3D.Infrastructure/Releases/ReleaseWriteStore.cs).

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
