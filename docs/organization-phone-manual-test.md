# Test trùng số điện thoại tổ chức

Phạm vi: email/OTP registration, Google organization onboarding và PATCH organization. Giữ flow OTP/password/username/session/Google recovery hiện có. Không có endpoint mới hoặc uniqueness cho phone cá nhân.

## Policy và response

Phone lưu ở `organizations.phone`. Normalization trim, bỏ khoảng trắng ASCII/dấu `-`/ngoặc; giữ `+` đầu và 6–15 chữ số ASCII. `(070) 636-4866` và `0706364866` trùng; `0706364866` và `+84706364866` khác nhau. Số của inactive/soft-deleted vẫn được giữ. Legacy/admin NULL được phép, đăng ký mới vẫn bắt buộc phone hợp lệ.

Register organization và Google completion trả409 như dưới; PATCH dùng `errors.phoneNumber`:

```json
{
  "title": "Số điện thoại đã được một tổ chức sử dụng.",
  "status": 409,
  "code": "ORGANIZATION_PHONE_EXISTS",
  "errors": { "organizationPhoneNumber": ["Chọn số điện thoại khác cho tổ chức."] },
  "traceId": "<trace-id>"
}
```

Không trả tên/ID tổ chức giữ số. Chỉ SQLSTATE23505 cùng index `organizations_phone_normalized_key` được map thành phone conflict; email/username/slug giữ lỗi riêng.

## Deployment và preflight

1. Deploy binary có mapping lỗi trước khi bật index để tránh unique violation thành500; vẫn giữ các migration dependency của binary.
2. Dùng migration identity đã kiểm đúng host/database và thấy **toàn bộ** tổ chức, chạy `database/006_organization_phone_preflight.sql` trong transaction read-only. Không dùng login API với phạm vi đọc hạn chế để chứng minh dữ liệu toàn bảng sạch.
3. Kết quả phải rỗng. Có lỗi: danh sách chỉ có organization ID, lifecycle, số đã che, loại lỗi và số duplicate. Dừng deployment index; xử lý theo quy trình dữ liệu qua kênh vận hành kiểm soát. Không đưa số đầy đủ vào Git/log/API; không tự gộp/xóa/chọn tổ chức thắng.
4. Sau khi preflight sạch, áp migration additive `20261006110000_AddOrganizationPhoneUniqueness` bằng công cụ migration của deployment. Migration dùng cùng SQL kiểm lại dưới khóa ghi bảng, tạo expression index trong transaction; lỗi rollback. Không thêm cột phone hoặc backfill phone/revision. Lập lịch phù hợp vì index thường chặn ghi bảng trong thời gian tạo.
5. Xác minh history và index trên môi trường đích bằng truy vấn chỉ đọc:

```sql
SELECT "MigrationId" FROM public."__EFMigrationsHistory"
WHERE "MigrationId" = '20261006110000_AddOrganizationPhoneUniqueness';
SELECT indexdef FROM pg_indexes
WHERE schemaname = 'public' AND tablename = 'organizations'
  AND indexname = 'organizations_phone_normalized_key';
```

Không chạy cả schema Docs đè database. Không mở thêm quyền API hoặc reset dữ liệu. Migration mới chưa được áp lên Supabase trong task implementation này; fixture disposable không chứng minh deployment.

## Test trong Swagger

1. Chọn hai email test khác nhau, chưa có account. Theo [luồng đăng ký](registration-payos-manual-test.md), request OTP → verify → register organization A với `organizationPhoneNumber: "0706364866"`. Đổi thành số test còn trống nếu số ví dụ đã được dùng. Kỳ vọng201 AccountResponse, sau đó login riêng.
2. Xác minh email B để lấy proof mới, gọi register organization B với `(070) 636-4866`. Kỳ vọng409/code/field/traceId ở trên. Không tạo owner/org, không consume proof hoặc audit.
3. Giữ proof B, sửa phone thành `0706364867` và gọi lại trong TTL. Kỳ vọng201. Số sai định dạng vẫn400 theo field và không consume proof.
4. Với Google UID/email mới, [exchange rồi complete organization](google-auth-manual-test.md) dùng số của A:409/errors.organizationPhoneNumber. Sửa số khác còn trống bằng cùng proof chưa hết hạn:201 Authenticated. Kiểm email→Google, Google→email và Google→Google. Completion đã commit vẫn trả409 AlreadyCompleted khi replay cùng input; input khác409 IdempotencyKeyConflict, không cấp thêm phiên.
5. Authorize bằng OrganizationUser A; `GET /api/organizations/me` lấy ETag. `PATCH` với If-Match đó và `{"phoneNumber":"(070) 636-4866"}` thành công200. Lấy ETag mới; PATCH số của B:409/errors.phoneNumber. GET lại phải giữ representation/ETag trước conflict, không audit mới.
6. PATCH bỏ phone giữ nguyên; null/rỗng/sai định dạng400. Missing If-Match428, malformed400, stale412. Cùng current phone được phép, không chiếm số tổ chức khác.
7. Trên môi trường test được phép thay lifecycle, khóa/xóa mềm A rồi thử số A bằng email/Google khác: vẫn409. `0…` và `+84…` không tự coi là một số; user phone có thể trùng org phone.
8. Gửi hai request đăng ký khác email/proof cùng canonical phone đồng thời; tối đa một201, bên còn lại409, không500. Kiểm số account/org/receipt/session/audit và proof request thua chưa consume. Không dùng một proof cho hai email.

## Bằng chứng và giới hạn

- Source: mapping ở store chung, handler/controller field errors, Swagger409 và policy; không pre-check thay DB constraint.
- Test: PostgreSQL disposable, runtime role hạn chế quyền, cross-flow HTTP, direct concurrent writes, registration↔PATCH, rollback audit, proof retry, lifecycle, normalization parity, migration/history preservation và preflight blocking.
- Chưa nghiệm thu Supabase index, binary Azure, Firebase/Mailgun thật hoặc FE. Chỉ tick deployment sau khi history/index và các response trên môi trường đích đã được kiểm tra; không lấy build/mock pass thay bằng chứng này.

Preflight Supabase chỉ đọc ngày 06/10/2026: 6 tổ chức, 0 phone NULL; có 2 nhóm duplicate canonical (3 và 2 tổ chức), không thấy phone sai định dạng. Index/history migration phone chưa có. **Migration bị chặn bởi dữ liệu trùng**, không có DDL/DML, gộp/xóa hoặc đổi phone. Báo cáo ID/lifecycle/số che được giữ local trong kênh vận hành; không commit PII. Cần quyết định xử lý các tổ chức trùng rồi chạy lại preflight; số liệu này chỉ mô tả snapshot lúc kiểm tra, không bảo đảm dữ liệu chưa đổi sau đó.
