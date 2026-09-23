# Xác thực và tài khoản BE

## Chuẩn sản phẩm

Đọc [FR-AUTH](../../Docs/fire_evacuation_requirements.md), [workflow đăng ký/profile](../../Docs/fire-evacuation-training-workflows.md) và [technology contract](../../Docs/fire-evacuation-training-technology.md). Chuẩn hiện hành là:

- BE quản lý email/password local, password hash và Fire3D session. Supabase chỉ là PostgreSQL; không dùng Supabase Auth.
- Firebase xác minh Google identity. Firebase không cấp role nghiệp vụ. Role, trạng thái và tenant lấy từ PostgreSQL.
- Trainee đăng ký local với email, username, password và confirm password. OrganizationUser đăng ký với email/password và hồ sơ organization; username cá nhân tùy chọn. Client không tự chọn role/tenant.
- Google identity mới phải qua onboarding ngắn hạn và chọn Trainee hoặc OrganizationUser. Identity đã liên kết đăng nhập theo role/tenant hiện có. Không tự nối tài khoản chỉ vì email trùng.
- Trainee username lowercase, duy nhất toàn hệ thống và hoàn tất lúc đăng ký/onboarding; không yêu cầu username ở game start.
- Forgot/Reset Password là một luồng: email link một lần đặt mật khẩu mới. Change Password là luồng trong phiên đăng nhập, xác minh mật khẩu hiện tại rồi nhận mật khẩu mới. Cả hai thu hồi các refresh session theo contract; chỉ reset gửi Mailgun.
- Google UID nullable/unique khi liên kết. Link Google vào tài khoản local cần chứng minh đã xác thực tài khoản local; không tự động link.

## Hiện trạng source BE trên nhánh main đã đối chiếu

| Năng lực | Source hiện có | Khác biệt so với contract đích |
|---|---|---|
| Local registration | `POST /api/auth/register` tạo Trainee bằng email/password/fullName | Thiếu username và confirm password trong contract; chưa có đăng ký OrganizationUser tự phục vụ |
| Local login/session | Login, refresh rotation, logout và `/api/auth/me` dùng Fire3D access/refresh token | Không đổi thành Firebase-only; rà response/client storage theo API guide |
| Google | `POST /api/auth/login-firebase`; tài khoản Google mới hiện được tạo thành Trainee | Chưa có onboarding chọn role và hoàn tất hồ sơ OrganizationUser; chưa có link Google tường minh |
| Profile | `GET/PATCH /api/auth/me` hiện đọc/cập nhật thông tin tên cơ bản | Chưa có username, ETag, avatar S3 hoặc organization profile PATCH như Docs |
| Reset password | Forgot tạo job bền vững; worker gửi Mailgun; reset tiêu thụ token, đổi hash, thu hồi phiên và ghi audit trong user transaction | Core local reset đã có code; cần đối chiếu bảng token với schema target và kiểm thử rollback/race. Chưa khẳng định Mailgun production đã gửi thật |
| Change password | `POST /api/auth/change-password` kiểm tra mật khẩu hiện tại, lưu mật khẩu mới, vô hiệu token reset và thu hồi phiên trong user transaction | Đã có code; không gửi email; cần giữ kiểm tra rollback/session cũ khi đồng bộ schema |

Chi tiết request/response hiện tại nằm ở [API guide](api-docs.md). Chi tiết worker/token/cấu hình nằm ở [password-reset.md](password-reset.md). Không copy contract endpoint mục tiêu thành route “đang có” nếu controller chưa triển khai.

## Hướng triển khai khi sửa auth

1. Thêm đúng các endpoint register/onboarding/profile từ mục 9 của Docs technology; giữ role/tenant do backend quyết định.
2. Lưu và kiểm username toàn cục lowercase; collect username Trainee ở đăng ký/onboarding; không thêm game-start gate.
3. Hoàn tất Google onboarding idempotent, giữ role/tenant của UID đã link, yêu cầu xác thực local trước khi link vào email local hiện có.
4. Bổ sung ETag/profile revision, organization profile và avatar private S3 theo contract; không nhận URL/avatar data tùy ý từ client.
5. Giữ reset và change thành hai use case riêng nhưng dùng cùng chính sách transaction, token/session revoke và audit của Docs. Core transaction hiện có; đối chiếu mapping schema token và kiểm thử PostgreSQL trước khi coi luồng hoàn tất. Không dùng Firebase password reset cho local password.

## Quy tắc bảo mật

- Không lưu/trả password plaintext, password hash, reset token thô, Firebase Admin credential, access/refresh token trong log.
- Không nhận role, organizationId, trạng thái hoặc actor tin cậy từ request của client.
- Tài khoản, organization và session phải được xác minh ở BE; mọi truy vấn nghiệp vụ áp dụng role và tenant scope ở server.
- Khi login/password reset chạy đồng thời, bảo đảm khóa user/transaction tránh cấp phiên từ mật khẩu cũ sau reset commit.
- Không tuyên bố xác minh Google, gửi Mailgun, revoke token, migration hoặc quyền DB production đạt nếu chỉ mới có unit/mock test.

## Kiểm tra

Chạy auth test phù hợp theo [integration-tests.md](integration-tests.md). Bao gồm ít nhất: đăng ký role đúng và email/username collision; Google UID mới/đã liên kết/trùng email/race; onboarding retry; profile ETag; reset token hết hạn/dùng lại; change sai mật khẩu cũ; revoke session và rollback khi audit/DB lỗi. Tách test local PostgreSQL khỏi provider test thật.
