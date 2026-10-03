# Test tay đăng ký theo form và thanh toán PayOS

Swagger local: `http://localhost:5173/swagger/index.html`. Phải chạy đúng worktree/commit có PayOS; checkout gốc cũ không có các endpoint này. Các ID dưới đây là placeholder: copy ID/ETag từ response hiện tại, không dùng Building ID thay Quotation ID.

## Đăng ký: form → OTP → tạo account

FE giữ form trong bộ nhớ khi chuyển sang bước OTP; không lưu password trong URL, localStorage hay sessionStorage. Nút **Xác thực và đăng ký** gọi hai API nối tiếp: verify thành công rồi gửi toàn bộ form và proof. Swagger test từng request riêng.

1. `POST /api/auth/registration/request-otp`, không Bearer:

   ```json
   { "email": "email-moi-ban-so-huu@example.com" }
   ```

   Email mới: `202` nhận yêu cầu, worker gửi mã sáu số. Email đã có (kể cả khác chữ hoa/thường, account khóa): `409 EMAIL_EXISTS`, `errors.email`; không tạo job. Kiểm tra inbox/spam để chứng minh delivery; 202 không chứng minh email đã nhận.

2. `POST /api/auth/registration/verify-otp`:

   ```json
   { "email": "email-moi-ban-so-huu@example.com", "otp": "012345" }
   ```

   Dùng mã thực nhận, giữ số 0 đầu. `200` trả `registrationToken` và `expiresAt` (proof 15 phút). Bước này chưa tạo account.

3. `POST /api/auth/register/trainee`, gửi toàn bộ form:

   ```json
   {
     "email": "email-moi-ban-so-huu@example.com",
     "username": "test_otp_001",
     "password": "Test123",
     "confirmPassword": "Test123",
     "fullName": "Test User",
     "dob": "2004-07-29",
     "gender": "Other",
     "phoneNumber": "0706364866",
     "registrationToken": "<proof-tu-buoc-2>"
   }
   ```

   `201` trả account Trainee đã xác minh; **chưa cấp JWT**. DOB là YYYY-MM-DD. Password 6–128 ký tự; confirm khớp chính xác.

4. Với OrganizationUser, dùng **email mới khác**, làm lại bước 1–2 rồi gọi `POST /api/auth/register/organization`:

   ```json
   {
     "email": "email-owner-moi@example.com",
     "password": "Test123",
     "confirmPassword": "Test123",
     "fullName": "Owner",
     "dob": "2004-07-29",
     "gender": "Other",
     "phoneNumber": "0706364866",
     "organizationName": "Organization test PayOS",
     "organizationAddress": "123 Nguyen Van Linh",
     "organizationPhoneNumber": "0706364866",
     "registrationToken": "<proof-cua-email-owner>"
   }
   ```

   `201`: role OrganizationUser và organizationId do server tạo. Không gửi role/tenant. Đăng nhập bằng `POST /api/auth/login`, rồi Authorize bằng accessToken của account này.

5. Gửi lại mã: `POST /api/auth/resend-verification`, cùng body email. Trong 60 giây trả 202 nhưng không tạo mã mới; sau 60 giây tạo OTP mới, mã/proof cũ bị vô hiệu. OTP hết hạn sau 10 phút. Vượt 5 request/email/giờ hoặc 20/IP/giờ: `429 OTP_RATE_LIMITED` và Retry-After. Sai form trả 400/errors theo field và không consume proof; sửa form rồi dùng lại proof còn hạn. Proof đã dùng không tạo account thứ hai.

`/check-email/` là trang demo OTP/proof, không phải form đăng ký đầy đủ của FE. Khi đổi email, demo xóa proof cũ. Tích hợp FE thật vẫn cần giữ form, khóa nút trong hai request, xử lý lỗi rồi chỉ chuyển sang login sau 201.

## PayOS: Accepted → link/QR → Paid → entitlement

### Chuẩn bị bằng Swagger

1. Admin tạo package test `POST /api/admin/service-packages`:

   ```json
   { "code": "PAYOS_TEST_2000", "name": "PayOS test 1 month", "unitPrice": 2000, "durationMonths": 1, "isActive": true }
   ```

2. Authorize OrganizationUser. `GET /api/buildings`: chọn Building active **thuộc chính organization này**, có tên/địa chỉ (hoặc tạo bằng API Building). Package trên trả servicePackageId.
3. `POST /api/billing/quotations`, `Idempotency-Key: quote-payos-001`:

   ```json
   { "items": [{ "buildingId": "<buildingId>", "servicePackageId": "<servicePackageId>", "purchaseAction": "New" }] }
   ```

   Lưu **quotationId** và ETag response. Admin issue quotation bằng `POST /api/admin/quotations/{quotationId}/issue`, điền `If-Match` chính xác cả dấu nháy. Body (thay validUntil bằng UTC trong tương lai):

   ```json
   { "taxAmount": 0, "terms": "Test PayOS 2000 VND", "validUntil": "2026-12-31T00:00:00Z" }
   ```

   Sau issue lấy **ETag mới** từ response/header hoặc GET quotation.
4. Authorize OrganizationUser, `POST /api/billing/quotations/{quotationId}/accept`, If-Match của bản Issued, không body. Kỳ vọng Accepted; không có tiền/entitlement ở bước này.

### Checkout và kiểm tra

5. `POST /api/payments/payos/create`, OrganizationUser, `Idempotency-Key: payos-test-001`:

   ```json
   { "quotationId": "<quotationId-Accepted-con-han>" }
   ```

   `201` khi link Ready; `202` khi đang xử lý/cần reconcile. Lưu `checkoutId`, `paymentRequestId` khi có, orderCode. Client không gửi amount hay callback. `PAYOS_DISABLED` nghĩa cấu hình hiệu lực chưa bật; lỗi executor/grants là blocker riêng.
6. `GET /api/payments/payos/checkouts/{checkoutId}` để poll. Mở `checkoutUrl`; PayOS hiển thị QR. `qrCode` là chuỗi payload QR, không phải URL ảnh.
7. **Chỉ tự thanh toán sau khi webhook công khai đúng phiên bản đã sẵn sàng.** Localhost không nhận webhook internet; cần deploy API hoặc tunnel HTTPS trỏ đúng API local và đăng ký `/api/payments/payos/webhook` trên PayOS. Trang return/cancel phải tồn tại tại callback host. Mở link/QR local chưa chứng minh ghi nhận tiền hoạt động.
8. Sau khi bạn thanh toán, `GET /api/payments/payos/requests/{paymentRequestId}`: kiểm `paymentStatus=Paid`, `transactionStatus=Applied`, từng dòng `Succeeded`. `GET /api/billing/entitlements?buildingId=<buildingId>`: đúng Building, khoảng kỳ tháng, chỉ một entitlement từ dòng đã thanh toán.
9. Gọi lại create cùng key/body: cùng operation, không thêm giao dịch. Admin `POST /api/admin/payments/payos/checkouts/{checkoutId}/reconcile` chỉ enqueue recovery, không tự ghi Paid.
10. Với quotation/checkout **khác chưa thanh toán**, `POST /api/payments/payos/requests/{paymentRequestId}/cancel`, `Idempotency-Key: cancel-test-001`, không body. Đọc checkoutStatus để xác nhận hủy; `200` của replay chỉ trả trạng thái hiện hành. Yêu cầu cancel mới cho payment Paid: 409 PAYMENT_ALREADY_PAID.

Không giả webhook/chữ ký để ép Paid. Query `status=PAID` ở return URL chỉ điều hướng, không cấp quyền. Kiểm thử provider thật, worker, public webhook và entitlement là nghiệm thu riêng với test PostgreSQL/mock.

## Bằng chứng lượt kiểm tra 2026-10-03

- Solution build: 0 warning/error. Auth/Billing/PayOS: 225 passed, 0 failed/skipped; dùng PostgreSQL disposable loopback, provider giả lập, worker thật tắt trong test. Demo OTP: 4 test Node (`node --test Fire3D/Fire3D.AuthTests/browser/check-email.test.cjs`), không phải E2E trên browser thật.
- Supabase: migration email additive đã áp; logins request/webhook và runtime có quyền giới hạn, không direct financial DML; admin credential giữ riêng MigrationConnection, DefaultConnection dùng runtime. Không reset hoặc tạo account/payment bằng test tự động.
- HTTP local: Swagger/OpenAPI và hai trang payment trả 200; request-otp cho email đã tồn tại trả 409, code/field/traceId đúng. OTP và PayOS worker đã bật local; chưa gửi mã thật hay tạo link/thu tiền tự động.
- Host Azure hiện trả 404 cho hai trang callback; webhook/domain deployment và giao dịch thật chưa nghiệm thu. Cần deploy/register webhook hoặc tunnel đúng API trước khi chuyển tiền. FE repo không có trong workspace; đã cập nhật contract Docs và demo, chưa chứng minh FE form đã tích hợp.
