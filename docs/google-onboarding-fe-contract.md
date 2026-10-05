# Google onboarding — hợp đồng FE

Contract trên nhánh BE `feature/google-onboarding-session`, thay thế response AccountResponse-only của completion trên baseline `e78dc7e`. Đây là source mới, chưa chứng minh deployment/Firebase/FE đã cập nhật. Ví dụ mock: [google-onboarding.json](fixtures/google-onboarding.json); test tay: [Google guide](google-auth-manual-test.md).

1. Firebase Google Sign-In → Firebase ID token → `POST /api/auth/login-firebase`, body JSON string.
2. `Authenticated`: lưu authentication hợp lệ và điều hướng theo role BE. `OnboardingRequired`: giữ proof **trong bộ nhớ**, hiển thị email/displayName từ `onboarding`, chọn trainee/organization và các trường. Không password hoặc OTP. Proof15 phút; không URL/web storage.
3. Complete gửi `onboardingToken = onboarding.token`, accountType canonical `trainee` hoặc `organization`. Role-name aliases chỉ để client cũ chuyển đổi. Server từ chối client email/UID/organizationId/role và số enum. FullName omitted dùng tên Google; null khi không có tên.
4. Complete201 `{status:"Authenticated",authentication:{accessToken,refreshToken,user}}` → lưu phiên rồi gọi `/api/auth/me`. Không cần exchange lần hai khi đã nhận response này. Không có expiresAt trong authentication; expiry ở onboarding chỉ là TTL proof.
5. Mất response hoặc409 ONBOARDING_ALREADY_COMPLETED → lấy Firebase ID token còn hợp lệ (refresh bằng SDK nếu cần), exchange lại. Không gọi complete nhiều lần để xin token, không tạo form/proof khác khi biết đã commit. Khác input trên proof đã consume trả409 IDEMPOTENCY_KEY_CONFLICT.
6. Validation400/errors theo field: giữ form và sửa; proof chưa consume. Proof invalid/expired400: thử lại Google. Username409: chọn tên khác. Email collision409 ACCOUNT_LINK_REQUIRED: hướng local login, không tự link. Account disabled403: không retry để vượt lifecycle. 429/503: tuân Retry-After, không retry vòng lặp liên tục.

Nested proof là contract mới; root onboardingToken/expiresAt deprecated nhưng cùng giá trị. User.role trả tên enum `Trainee`/`OrganizationUser`; role số chỉ là tương thích client cũ, accountType số không được chấp nhận. Link explicit `/api/me/link-google` là use case khác, cần live local session và currentPassword; FE issue này chỉ hướng local login khi trùng email.

Triển khai migration `20261006090000_AddGoogleOnboardingDisplayName` trước binary, cập nhật FE cùng đợt. Phải smoke test hai loại account, bearer/refresh và recovery sau mất response với Firebase test project. Repo FE không có trong workspace này; các fixture là bàn giao contract, không phải FE đã tích hợp.
