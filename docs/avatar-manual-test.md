# Test Avatar private S3

Nguồn: FR-AUTH-07/09; route tại `AvatarController`, luồng tại `AvatarService`, persistence/recovery tại `AvatarStore`. Cả Trainee và OrganizationUser sử dụng cùng API với JWT của chính mình.

## Multipart trên Swagger

1. Login, Authorize bằng access token; gọi `GET /api/auth/me`, copy response header `ETag` gồm dấu ngoặc kép.
2. `POST /api/me/avatar/upload`: điền `If-Match`, chọn ảnh trong field **file**. JPEG/PNG/WebP, tối đa 5 MiB, 4096×4096, một frame. Kỳ vọng 200, revision/ETag mới và `url`.
3. Copy **toàn bộ giá trị `url`** vào tab ẩn danh. Đây là URL HTTPS S3 có query chữ ký, không phải URL API. URL đã ký còn hạn không cần Bearer; `/api/me/avatar` cần Bearer để BE xác minh chủ ảnh rồi cấp URL. Không bỏ query, không thêm JWT vào query và không dùng object URL chưa ký.
4. URL hết hạn sau 5 phút: gọi `GET /api/me/avatar` với Bearer để lấy URL mới. Không mở public bucket/ACL để sửa lỗi URL hết hạn.
5. DELETE với ETag mới nhất trả 204 và ETag mới; GET avatar trả 404. Xóa reference là tức thời; object S3 được worker xử lý sau commit, có retry khi S3 lỗi.

## Upload intent/complete

Gọi `POST /api/me/avatar/upload-intent` với `{ "contentType": "image/png", "contentLength": <số byte thực> }`. PUT bytes lên `uploadUrl` với Content-Type khớp, trong 5 phút. Gọi complete với `{ "uploadId": "<ID>" }` và ETag profile ban đầu.

Replay complete trong 24 giờ dùng cùng uploadId **và ETag ban đầu**: trả URL đọc mới nếu avatar còn hiện hành. ETag/input khác trả 412; avatar đã thay/xóa trả 409 `AVATAR_UPLOAD_SUPERSEDED`. Candidate hết lease không được copy lại/adopt; tạo upload mới. Store kiểm thời gian database sau khi lấy khóa, không dùng timestamp request cũ để gia hạn quyền.

## Khi link không mở được

- Nếu URL là host BE `/api/me/avatar`, 401 khi thiếu Bearer là đúng; lấy `url` trong JSON.
- Nếu URL là S3, giữ nguyên query `X-Amz-*`; kiểm expiry, clock/region/bucket và XML error code. Không chia sẻ signed URL trong log công khai vì ai có URL còn hạn đều đọc được.
- IAM BE cần GetObject/PutObject/DeleteObject trên `avatars/staging/*` và `avatars/users/*`; conditional copy cần đọc source và ghi destination. Dùng private bucket, Block Public Access. Nếu dùng SSE-KMS, kiểm quyền KMS tương ứng.
- Browser PUT trực tiếp cần S3 CORS cho origin FE, method PUT và Content-Type. Multipart đi qua BE không cần CORS S3 cho upload. Mở ảnh trong tab không phụ thuộc API Bearer; CORS cần cho fetch/canvas tùy cách FE đọc ảnh.
- Không coi URL ký được là bằng chứng object tồn tại hoặc IAM production đúng. Đối chiếu request ID/log server an toàn khi provider báo lỗi.

## Bằng chứng tự động và giới hạn

`AvatarRecoveryPostgresTests` kiểm lease hết hạn kể cả timestamp cũ, role runtime hạn chế/RLS, cross-owner intent lookup, rollback audit, candidate recovery/retry, complete/delete cạnh tranh và receipt lease cũ. Fixture dựng EF schema rồi áp SQL migration Avatar/ACL thực tế, không chứng minh toàn bộ target schema v7 hoặc grants Supabase.

`AvatarS3AdapterTests` kiểm stream bị chia nhỏ, giới hạn đọc dù metadata sai, pin source ETag và SDK ký URL offline với credential giả. Test này không gọi AWS. Recovery giữ grace một giờ trước cleanup candidate hết lease; không chứng minh S3 request treo vô hạn đã dừng. S3/FE/deployment và việc tab ẩn danh đọc object thật cần nghiệm thu riêng.
