# Google onboarding và liên kết local

Nguồn yêu cầu: Docs FR-AUTH-01/05/06/08/10, workflow §2.3. Firebase xác minh Google, PostgreSQL quyết định role/tenant. Không tự nối account bằng email. Các kiểm thử tự động dùng PostgreSQL disposable và provider giả; Firebase/client/deployment cần nghiệm thu riêng.

## Onboarding

1. FE dùng Firebase Google Sign-In để lấy **Firebase ID token**, không dùng Google OAuth access token. Gọi anonymous `POST /api/auth/login-firebase` với body JSON string `"<firebase-id-token>"`.
2. UID đã liên kết trả `Authenticated` và token BE. Email trùng account local trả `409 ACCOUNT_LINK_REQUIRED`: đăng nhập local rồi dùng link, không tạo account khác. UID/email mới trả `OnboardingRequired` và `onboarding: {token, expiresAt, email, displayName}`; chưa có account hoặc JWT BE. Email/displayName lấy từ identity đã xác minh, tên có thể null. Alias ngoài cùng `onboardingToken`/`expiresAt` giữ cùng giá trị trong giai đoạn chuyển đổi và sẽ bỏ trong phiên bản riêng. Proof có hạn 15 phút, chỉ giữ trong bộ nhớ FE.
3. Trong 15 phút gọi anonymous `POST /api/auth/google/onboarding/complete`:

```json
{
  "onboardingToken": "<proof từ exchange>",
  "accountType": "trainee",
  "username": "google_user",
  "fullName": "Google User",
  "dob": "2004-07-29",
  "gender": "Female",
  "phoneNumber": "0706364866"
}
```

Hoặc organization:

```json
{
  "onboardingToken": "<proof từ exchange>",
  "accountType": "organization",
  "fullName": "Organization Owner",
  "organizationName": "FET Test Organization",
  "organizationAddress": "123 Example Street",
  "organizationPhoneNumber": "0706364866"
}
```

Trainee bắt buộc username lowercase duy nhất, `[a-z0-9._-]{3,30}`. OrganizationUser tạo **tổ chức mới** và owner cùng transaction; chưa nhận username khi onboarding, có thể đặt sau qua profile. Profile tùy chọn theo validation local; DOB `YYYY-MM-DD`, không ở tương lai. Không gửi password, email, role, tenant hoặc organizationId: identity lấy từ proof và role được server chọn trong hai loại hợp lệ.

4. Thành công **201** `{ "status": "Authenticated", "authentication": { "accessToken": "<JWT BE>", "refreshToken": "<refresh>", "user": { "...": "AccountResponse" } } }`. Google-only không có password, email đã verified. Account, organization, receipt, cập nhật login, hash refresh token và hai audit Create/Login commit atomic. Dùng accessToken gọi `/api/auth/me` và refreshToken gọi `/api/auth/refresh`. Không có expiresAt trong authentication. Nếu không gửi fullName, BE dùng displayName đã lưu từ Google; cả hai thiếu thì null. Canonical accountType là `trainee`/`organization`; alias `Trainee`/`OrganizationUser` vẫn nhận, số enum/PlatformAdmin bị từ chối theo `errors.accountType`.
5. Retry cùng proof/nội dung chuẩn hóa trong 24 giờ trả **409 ONBOARDING_ALREADY_COMPLETED**, không cấp thêm phiên, kể cả TTL proof ban đầu đã qua. FE gặp mã này hoặc mất response thì lấy Firebase ID token hợp lệ và exchange lại để nhận phiên mới. Khác nội dung trả409 IDEMPOTENCY_KEY_CONFLICT. Hai proof cùng UID không tạo hai account. Proof sai400 ONBOARDING_TOKEN_INVALID; proof chưa dùng hết15 phút hoặc receipt quá24 giờ400 ONBOARDING_TOKEN_EXPIRED. Validation lỗi chưa consume proof. Account/tổ chức bị khóa chặn replay/exchange. Chờ khóa quá3 giây trả503 ONBOARDING_RETRY_REQUIRED và Retry-After:1.

Mỗi verified UID được tạo tối đa 10 proof/15 phút, vượt trả `429 GOOGLE_ONBOARDING_RATE_LIMITED` cùng Retry-After (giây tới lúc có quota, tối đa900). Không lưu raw proof/JWT/password vào receipt, không log Firebase token. Migration additive `20261005100000_AddGoogleOnboarding` tạo bảng hash/receipt, RLS/backend grants và unique UID; duplicate UID legacy làm migration dừng để xem xét, không tự gộp/xóa account. Migration mới `20261006090000_AddGoogleOnboardingDisplayName` bổ sung display_name nullable, không xóa dữ liệu cũ; chạy trước binary mới. Chưa áp Supabase trong task này.

## Liên kết vào tài khoản local

1. Đăng nhập local bằng `/api/auth/login`; Authorize Swagger bằng accessToken BE. Không dùng Firebase token ở ô Bearer.
2. Firebase Google Sign-In lấy ID token mới. Gọi `POST /api/me/link-google`:

```json
{
  "idToken": "<Firebase ID token Google>",
  "currentPassword": "<mật khẩu local hiện tại>"
}
```

BE kiểm live family/account/tổ chức, Google provider/email verified/revocation và password hiện tại. Không gửi userId, role, email hoặc organizationId. Link giữ email local, username, role và tenant; Google email có thể khác email local vì đã chứng minh quyền sở hữu cả hai identity. Không cho thay Google UID đã link hoặc chiếm UID thuộc account khác. Google-only không dùng route này để tạo mật khẩu.

3. Lần link mới trả200 `{user, alreadyLinked:false, requiresLogin:true}` và ETag mới; thu hồi toàn bộ session family/reset proof và ghi audit cùng transaction. FE xóa token local, đăng nhập lại qua local hoặc Google exchange. Access/refresh cũ trả401. Không có session/JWT mới trong response link.
4. Sau login lại, gửi cùng Google UID/password trả200 `alreadyLinked:true, requiresLogin:false`, không tăng revision/ghi audit/revoke phiên mới. Replay bằng session đã revoke vẫn401. Provider outage503 khác invalid identity401; sai password401, account/session unavailable401, pending email403, UID bị chiếm/thay thế409. Pending password-reset operation chặn **link mới** với409 `PASSWORD_RESET_PENDING` để giữ fence của local auth. Lỗi validation trả `errors.idToken/currentPassword`, `code` và `traceId`.
5. Thử sai password, Google UID của account khác, token Firebase revoked/non-Google/email chưa verified, account/org khóa và password thay đổi trong lúc provider xác minh. Không thay identity hoặc mất reset/session khi audit lỗi. Không gọi provider/S3 trong transaction DB.

## Kiểm tra trước deployment

- Regression timeout kiểm cả advisory lock và unique-index wait lúc EF ghi user/organization: trả503/Retry-After:1, rollback receipt và retry sau khi gỡ contention không tạo trùng account/session.
- Chạy migration với identity migration, xác minh login API kế thừa `fire3d_api` hoặc `fet3d_backend_executor`, có đúng quyền trên onboarding/users/organizations/audit. `anon`/`authenticated` không truy cập proof.
- Kiểm startup/DI và OpenAPI ở artifact mới. HTTP pass với fixture không chứng minh Firebase production hoạt động.
- Test thật chỉ sau cấu hình Firebase project đúng: hai loại account, account local trùng email, Google UID đã link, revoked Firebase token và provider outage. Không dùng DTO/table tồn tại để kết luận client đã tích hợp.
