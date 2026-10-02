# Deploy và test tay PayOS FET3D

Nguồn: FR-BILLING-03/06/07, workflows §11, technology §10; [SDK .NET](https://payos.vn/docs/sdks/back-end/net/), [webhook ACK](https://payos.vn/docs/du-lieu-tra-ve/webhook/) và [return URL](https://payos.vn/docs/du-lieu-tra-ve/return-url/). Không dùng return/cancel query để ghi Paid.

## Migration và cấu hình

Trong lượt triển khai code này **chưa áp runtime migration lên Supabase**, chưa gửi request provider thật và chưa thanh toán. Không reset database. Review SQL script từ đúng commit và áp trước khi bật checkout:

```powershell
dotnet ef migrations script 20261002100100_SyncBillingCatalogModel --idempotent --project Fire3D/Fire3D.Infrastructure --startup-project Fire3D/Fire3D.API --output payos-runtime.sql
```

Runtime thêm provider snapshot/binding, session family, lease/retry, sequence orderCode bounded và SQL gates; không xóa dữ liệu/cột legacy. Các migration Sync chỉ cập nhật metadata EF, không lặp DDL. AddPayosProvisioningGates cập nhật gate cho các môi trường đã áp một phần migration trước. Down migration không reset ledger; sửa bằng migration forward.

Mẫu biến môi trường, thay placeholder qua secret manager:

```text
PayOS__Enabled=false
PayOS__WorkerEnabled=true
PayOS__PollSeconds=30
PayOS__ClientId=<channel client ID>
PayOS__ApiKey=<secret>
PayOS__ChecksumKey=<secret>
PayOS__ReturnUrl=https://<host-BE>/billing/payment-return/
PayOS__CancelUrl=https://<host-BE>/billing/payment-cancel/
ConnectionStrings__DefaultConnection=<application identity, đúng DB, không có quyền ghi trực tiếp ledger>
ConnectionStrings__PayosRequestExecutor=<request login, đúng DB>
ConnectionStrings__PayosWebhookExecutor=<webhook login, đúng DB>
```

`appsettings.json` là cấu hình nền; User Secrets ghi đè trong Development, environment của deployment ghi đè cấu hình nền. Không đưa credential vào Git. Tắt `Enabled` chỉ ngăn checkout mới; giữ WorkerEnabled và credentials để xử lý các giao dịch đã tồn tại. Worker cần ứng dụng chạy liên tục; App Service bật Always On khi gói hỗ trợ.

Migration identity là identity quản trị dùng riêng khi deploy. Runtime executor login phải NOSUPERUSER/NOBYPASSRLS/NOCREATEDB/NOCREATEROLE, được membership vào đúng NOLOGIN executor, không vào ledger owner. Adapter kiểm quyền và database mục tiêu trước khi gọi gate. Không dùng `postgres` làm executor. General API identity chỉ đọc bảng tiền; chỉ ghi operation/receipt/inbox và lease/status retry của provisioning, không có INSERT/UPDATE/DELETE trên payment request/transaction hoặc INSERT entitlement. Không cấp webhook executor cho general API login. Các grant phải review theo DB/identity thật, không copy mật khẩu mẫu.

Query kiểm tra từ kết nối executor thực tế:

```sql
SELECT current_user,current_database();
SELECT rolsuper,rolbypassrls,rolcreatedb,rolcreaterole FROM pg_roles WHERE rolname=current_user;
SELECT pg_has_role(current_user,'fet3d_payos_ledger_owner','MEMBER'); -- false
SELECT has_table_privilege(current_user,'public.payment_transactions','INSERT,UPDATE,DELETE'); -- false
SELECT has_table_privilege(current_user,'public.payos_payment_requests','INSERT,UPDATE,DELETE'); -- false
```

## API mới

| API | Quyền / đầu vào |
|---|---|
| POST `/api/payments/payos/create` | OrganizationUser, `{ "quotationId": "<Accepted quotation UUID>" }`, Idempotency-Key |
| GET `/api/payments/payos/checkouts/{id}` | OrganizationUser cùng tenant hoặc PlatformAdmin |
| GET `/api/payments/payos/requests/{id}` | OrganizationUser cùng tenant hoặc PlatformAdmin; payment + transaction + từng dòng provisioning |
| POST `/api/payments/payos/requests/{id}/cancel` | OrganizationUser cùng tenant, Idempotency-Key |
| POST `/api/payments/payos/webhook` | Không JWT, bắt buộc chữ ký SDK PayOS hợp lệ |
| GET `/api/billing/entitlements` | OrganizationUser tenant mình; Admin filter organizationId/buildingId; page=1, pageSize=20 (max100) |
| POST `/api/admin/payments/payos/checkouts/{id}/reconcile` | PlatformAdmin; chỉ enqueue retry record cũ |

Create trả 201 khi link Ready lần đầu, replay 200; đang tạo/chưa rõ kết quả 202 với Location checkout. Giữ checkoutId và paymentRequestId riêng biệt, không dùng Building ID hoặc quotation ID làm request ID. Idempotency-Key 1–128 ASCII printable không space; input khác cùng key →409. Lỗi dùng code/traceId và errors theo field khi có. Cancel chưa được provider xác nhận trả202; thành công trả200. Payment race có thể thắng cancel, vì tiền đã nhận vẫn phải được ghi nhận.

## Test tay sau deploy

1. Xác nhận đúng commit/migration, API/worker chạy, các executor qua query kiểm quyền. Mở hai trang return/cancel bằng HTTPS host BE; Swagger dùng server `/` và cùng origin.
2. Trong kênh PayOS, đăng ký webhook HTTPS `https://<host-BE>/api/payments/payos/webhook`. Chỉ ACK payload đã qua SDK verification; probe/unknown order được lưu NeedsReconcile, không tạo entitlement. Đối chiếu provider log HTTP2xx; DB lỗi phải retry.
3. Admin tạo package test `unitPrice=2000`, `durationMonths=1`, một Building riêng active/có tên/địa chỉ. OrganizationUser tạo quotation; Admin issue với If-Match mới nhất, tax0/terms/hạn; OrganizationUser accept. Lưu quotation ID.
4. OrganizationUser gọi create với key `payos-test-001`. Nếu202, GET checkout đến khi Ready. Mở checkoutUrl và **bạn tự thanh toán**. Không nhập amount/orderCode/callback/tenant từ client.
5. GET request với paymentRequestId: `paymentStatus=Paid`, `transactionStatus=Applied`, `provisioningStatus=Succeeded`, từng dòng có entitlementId. GET entitlements kiểm đúng Building, startsAt/endsAt UTC và IsEffective. Đối chiếu một transaction, một entitlement mỗi dòng và provider reference trong PayOS.
6. Refresh return với `status=PAID`, replay create cùng key và admin reconcile: không thêm tiền hoặc entitlement. URL không phải bằng chứng payment.
7. Checkout mới chưa trả tiền: POST cancel với key riêng, kiểm Cancelled được provider xác nhận; không có entitlement. Một payment có hai Building phải kiểm từng dòng, không chỉ Paid.

## Điều tra / recovery

- `PAYOS_PROVIDER_TIMEOUT`/HTTP4xx/5xx: xem operation ID, attempt, errorCode; kiểm channel, cấu hình và outbound HTTPS. Không log secret/signature/HTML/bank payload.
- `PAYOS_AWAITING_VERIFIED_WEBHOOK`: GET provider báo paid/partial nhưng ledger chưa có webhook đúng. Kiểm delivery provider, yêu cầu redelivery webhook; API reconcile không tự ký hoặc ghi Paid từ GET.
- `PAYOS_AWAITING_BIND`: inbox đã nhận trước bind; kiểm checkout/provider result và retry cùng orderCode. Nếu quyền/hạn đã đổi và bind vẫn bị chặn, record được giữ để xử lý thủ công, không tự bỏ tiền.
- `PAYOS_UNKNOWN_ORDER`/`PAYOS_QUOTATION_ALREADY_PAID`/snapshot mismatch: giữ record/quarantine, đối chiếu PayOS. Không đổi amount, signature hay provenance để ép apply; hoàn tiền tự động ngoài phạm vi.
- `PAYOS_BUILDING_UNAVAILABLE`: tiền vẫn Paid; phục hồi Building/scope hợp lệ rồi admin reconcile. `PAYOS_NEW_ALREADY_ENTITLED`: hai đơn New cạnh tranh cho cùng Building, cần xử lý thương mại thủ công, không cấp kỳ chồng nhau.
- Sau10 failure liên tiếp, job dừng tự retry nhưng còn NeedsReconcile/error. Admin reconcile reschedule các job còn lỗi của checkout; dòng Succeeded/Applied không đổi. Object/record chưa có trong checkout không được tự giả lập ledger.
- Lease60s, fencing và transaction giúp retry sau restart; stale attempt không finalize. SQL/admin không được xóa lịch sử payment/entitlement.

- Link đã Paid nhưng mất response create: recovery bind đúng reservation khi GET đã xác minh toàn bộ amount; chỉ webhook đã xác minh mới ghi Paid/Applied. Nếu gate từ chối do quyền/hạn thay đổi thì giữ record để xử lý, không ép cấp quyền.
- Crash sau claim thứ10 có thể để job Pending với lease hết hạn. Admin reconcile reschedule cả trạng thái này, không chỉ NeedsReconcile; không chiếm lease còn hiệu lực.
- SDK adapter chỉ coi HTTP404 là xác nhận không tìm thấy. HTTP200 với business code chưa nhận diện vẫn là lỗi cần đối soát, không tự create lại. Khi nghiệm thu provider thật phải lưu contract của trường hợp order không tồn tại; không đoán code từ ví dụ ngoài tài liệu chính thức. Checkout chưa bind và expired nhưng không có xác nhận terminal vẫn giữ NeedsReconcile để tránh mở link thứ hai không rõ provenance.

Test tự động chứng minh logic/DB fixture, không chứng minh bank/provider hoặc grants production. Reminder5ngày, revenue, AI settlement, refund, eInvoice, FE billing đầy đủ và publish/playtest/training gate vẫn là backlog riêng.
