# Google onboarding và liên kết local

Nguồn yêu cầu: Docs FR-AUTH-01/05/06/08/10, workflow §2.3. Firebase xác minh Google, PostgreSQL quyết định role/tenant. Không tự nối account bằng email. Các kiểm thử tự động dùng PostgreSQL disposable và provider giả; Firebase/client/deployment cần nghiệm thu riêng.

## Onboarding

1. FE dùng Firebase Google Sign-In để lấy **Firebase ID token**, không dùng Google OAuth access token. Gọi anonymous `POST /api/auth/login-firebase` với body JSON string `"<firebase-id-token>"`.
2. UID đã liên kết trả `Authenticated` và token BE. Email trùng account local trả `409 ACCOUNT_LINK_REQUIRED`: đăng nhập local rồi dùng link, không tạo account khác. UID/email mới trả `OnboardingRequired`, `onboardingToken`, `expiresAt`; chưa có account hoặc JWT BE.
3. Trong 15 phút gọi anonymous `POST /api/auth/google/onboarding/complete`:

```json
{
  "onboardingToken": "<proof từ exchange>",
  "accountType": "Trainee",
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
  "accountType": "OrganizationUser",
  "fullName": "Organization Owner",
  "organizationName": "FET Test Organization",
  "organizationAddress": "123 Example Street",
  "organizationPhoneNumber": "0706364866"
}
```

Trainee bắt buộc username lowercase duy nhất, `[a-z0-9._-]{3,30}`. OrganizationUser tạo **tổ chức mới** và owner cùng transaction; chưa nhận username khi onboarding, có thể đặt sau qua profile. Profile tùy chọn theo validation local; DOB `YYYY-MM-DD`, không ở tương lai. Không gửi password, email, role, tenant hoặc organizationId: identity lấy từ proof và role được server chọn trong hai loại hợp lệ.

4. Thành công `201 AccountResponse` Google-only (`password_hash` null), email đã verified. Gọi lại exchange với Firebase ID token để nhận token BE; không có phiên tự động từ complete.
5. Lặp complete cùng proof/nội dung đã chuẩn hóa trong 24 giờ sau commit trả `200` cùng account, kể cả TTL proof ban đầu đã qua. Khác nội dung trả `409 IDEMPOTENCY_KEY_CONFLICT`. Hai proof khác nhau cùng UID không tạo hai account. Proof chưa dùng hết 15 phút trả `400 INVALID_ONBOARDING_TOKEN`; account/tổ chức đã khóa chặn replay.

Mỗi verified UID được tạo tối đa 10 proof/15 phút, vượt trả `429 GOOGLE_ONBOARDING_RATE_LIMITED`. Không lưu raw proof/JWT/password vào receipt, không log Firebase token. Migration additive `20261005100000_AddGoogleOnboarding` tạo bảng hash/receipt, RLS/backend grants và unique UID; duplicate UID legacy làm migration dừng để xem xét, không tự gộp/xóa account. Chưa áp Supabase trong task này.

## Kiểm tra trước deployment

- Chạy migration với identity migration, xác minh login API kế thừa `fire3d_api` hoặc `fet3d_backend_executor`, có đúng quyền trên onboarding/users/organizations/audit. `anon`/`authenticated` không truy cập proof.
- Kiểm startup/DI và OpenAPI ở artifact mới. HTTP pass với fixture không chứng minh Firebase production hoạt động.
- Test thật chỉ sau cấu hình Firebase project đúng: hai loại account, account local trùng email, Google UID đã link, revoked Firebase token và provider outage. Không dùng DTO/table tồn tại để kết luận client đã tích hợp.
