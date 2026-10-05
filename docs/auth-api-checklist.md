# Checklist endpoint auth hiện tại

Nguồn sản phẩm: FR-AUTH-01,04–13 và workflow §2 trong Docs. Nguồn thực thi: `AuthController`, `GoogleOnboardingController`, `GoogleLinkController`, `AvatarController`, `OrganizationProfileController`, DI trong Program/AuthenticationExtensions. Phạm vi bảng là identity/auth/profile/Avatar, không gồm API quản trị tài khoản hoặc analytics tổ chức.

✅ = có đường thực thi source, không đồng nghĩa provider/deployment/FE hoàn tất. Các test đọc metadata và HTTP dùng PostgreSQL disposable, Firebase/S3/mail giả lập; giới hạn từng nhóm ở dưới. Có **28 cặp method–route** trong phạm vi này; contract test đối chiếu bảng với OpenAPI để phát hiện route thêm/bỏ.

| Method | Route | Code | Quyền/input chính |
| --- | --- | --- | --- |
| POST | `/api/auth/login` | ✅ | Public; email/password; không áp quy tắc password mới vào login |
| POST | `/api/auth/login-firebase` | ✅ | Public; Firebase ID token JSON string; Google verified/revocation |
| POST | `/api/auth/register` | ✅ | Alias Trainee; deprecated, cùng proof/validation |
| POST | `/api/auth/register/trainee` | ✅ | Public; toàn bộ form + registrationToken; server gán role |
| POST | `/api/auth/register/organization` | ✅ | Public; form organization + registrationToken; owner/org atomic |
| POST | `/api/auth/registration/request-otp` | ✅ | Public; email; gửi mã đầu, không tạo account |
| POST | `/api/auth/registration/verify-otp` | ✅ | Public; email/OTP sáu số; trả proof, không tạo account |
| POST | `/api/auth/resend-verification` | ✅ | Public; gửi OTP mới sau cooldown; không gửi link mới |
| POST | `/api/auth/verify-email` | ✅ | Deprecated; chỉ link pending legacy |
| POST | `/api/auth/refresh` | ✅ | Public; refresh token; 10 request/IP/phút/instance |
| POST | `/api/auth/logout` | ✅ | Bearer; revoke family hiện hành |
| POST | `/api/auth/logout-all` | ✅ | Bearer; revoke mọi family và push binding |
| GET | `/api/auth/me` | ✅ | Bearer; profile + ETag; signed URL avatar nếu có |
| PATCH | `/api/auth/me` | ✅ | Bearer + If-Match; fullName/username/dob/gender/phoneNumber |
| PUT | `/api/auth/devices` | ✅ | Bearer + X-Installation-Key; UUID/token binding |
| DELETE | `/api/auth/devices/{deviceUuid}` | ✅ | Bearer + X-Installation-Key; revoke push binding |
| POST | `/api/auth/forgot-password` | ✅ | Public; email; 202 chung, Google-only không tự tạo password |
| POST | `/api/auth/reset-password` | ✅ | Public; reset token + newPassword; one-use/revoke/audit |
| POST | `/api/auth/change-password` | ✅ | Bearer + currentPassword/newPassword; live family kiểm dưới khóa |
| POST | `/api/auth/google/onboarding/complete` | ✅ | Public + proof15m; chỉ Trainee/OrganizationUser; login riêng |
| POST | `/api/me/link-google` | ✅ | Bearer + currentPassword + Google token; giữ role/tenant/email |
| GET | `/api/organizations/me` | ✅ | OrganizationUser; profile tổ chức + ETag |
| PATCH | `/api/organizations/me` | ✅ | OrganizationUser + If-Match; name/address/phoneNumber |
| POST | `/api/me/avatar/upload` | ✅ | Bearer + If-Match; multipart IFormFile `file` |
| POST | `/api/me/avatar/upload-intent` | ✅ | Bearer; contentType/contentLength; signed PUT5m |
| POST | `/api/me/avatar/complete` | ✅ | Bearer + If-Match; uploadId; receipt replay24h |
| GET | `/api/me/avatar` | ✅ | Bearer; lấy signed S3 URL5m, URL đọc không cần API Bearer |
| DELETE | `/api/me/avatar` | ✅ | Bearer + If-Match; reference/audit/cleanup atomic |

## Kiểm chứng và phần chưa xong

| Nhóm | Source/test | Còn cần nghiệm thu |
| --- | --- | --- |
| Local auth/password | ✅ PostgreSQL restricted role, replay, family/lifecycle, rollback và HTTP regression | ❌ Migration password gate/deployment, Mailgun thật và latency Azure của binary mới |
| OTP/register | ✅ Form → OTP → proof → account → login, quota/cooldown/collision có regression | ❌ FE form hoàn chỉnh và inbox OTP trên bản deploy mới |
| Google | ✅ Proof/link/onboarding, ownership/race/replay, provider timeout giả lập | ❌ Migration onboarding Supabase, Firebase project thật, FE/Mobile integration |
| Profile/organization | ✅ Converter giữ omitted/null; OpenAPI có field, header và role đúng; ETag regression | ❌ Client/deployment PATCH trên binary mới |
| Avatar | ✅ Decoder, pin ETag, bounded/fragmented stream; PG lease/rollback/recovery/cleanup/race và SDK signing offline | ❌ IAM/CORS/object AWS thật, crash/timeout production, tab ẩn danh đọc ảnh thật |
| Device | ✅ Installation proof/family/rotate/revoke có source/test | ❌ FCM thật và client secure installation storage |
| Swagger | ✅ Schema PATCH/multipart/proof/enum/headers và security metadata; relative server `/`; HTTP contract checks | ❌ OpenAPI/binary deploy mới; không chứng minh provider hoạt động |

Không đánh dấu toàn bộ audit hoàn tất từ bảng này. Google unlink, tạo local password cho Google-only và module ngoài auth không nằm trong đợt này. Không reset DB, thêm Redis hoặc sửa billing/training flow.

## Test tay sau khi deploy

- [Google onboarding/link](google-auth-manual-test.md).
- [Local password recovery/Google exchange](auth-local-google-manual-test.md).
- [OTP/register cả hai loại tài khoản](registration-payos-manual-test.md) — chỉ dùng phần register khi test auth.
- [Avatar và link S3 trong tab ẩn danh](avatar-manual-test.md).

Swagger: dùng ETag từ GET cho PATCH/upload/delete; missing428, malformed400, stale412. Profile cá nhân bỏ qua giữ nguyên, null xóa DOB/gender/phone; fullName/username null giữ nguyên. Profile tổ chức field gửi null/rỗng bị từ chối. Installation secret không phải JWT/OTP/FCM token; tạo ngẫu nhiên32byte và giữ cùng UUID.
