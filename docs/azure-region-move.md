# Chuyển API sang Japan East (cùng vùng với Supabase)

**Lý do (BE#168).** Supabase của production ở `ap-northeast-1` (Tokyo), còn App Service `fire3d-api` và Redis ở Azure East Asia (Hồng Kông). Mỗi lần API gọi xuống DB mất khoảng 60 ms tiền mạng, trong khi SQL chạy dưới 1 ms. Mỗi request gọi DB 2–7 lần nối tiếp, nên một lần đọc mất 200–430 ms.

Supabase không có vùng Hồng Kông, nên cách làm là đưa API và Redis sang **Japan East**. Trong cùng vùng, mỗi lần gọi DB còn khoảng 1–3 ms.

## Đổi gì, giữ gì

| Đổi | Không đổi |
|---|---|
| App Service: tạo app mới ở Japan East | Supabase (DB, dữ liệu, role, connection string) |
| Azure Managed Redis: tạo mới ở Japan East | Domain `api.fet3d.io.vn`; URL webhook PayOS, Mobile, worker VM đều gọi qua domain này |
| Bản ghi DNS `api.fet3d.io.vn` | S3, Mailgun, Firebase/FCM, PayOS |
| Secret và biến GitHub dùng để deploy | Image Docker trên GHCR, migration |
| Vùng chạy hàm Vercel của FE: `hkg1` → `hnd1` | Worker VM: chỉ gọi ra ngoài qua domain nên không cần sửa |

Không có dữ liệu nào phải chuyển:
- Redis chỉ giữ hàng đợi giao job. Nguồn gốc của mỗi job vẫn là bảng outbox trong PostgreSQL.
- Chuyển lúc không có job đang chạy thì Redis mới bắt đầu trống mà không mất gì.

## 0. Trước khi làm

1. Hạ TTL của bản ghi CNAME `api` ở nhà cung cấp DNS xuống 300 giây. Làm sớm ít nhất bằng TTL hiện tại.
2. Chọn lúc không có job và checkout đang dở. Kiểm bằng truy vấn chỉ đọc; cả ba dòng phải là 0:
   ```sql
   select count(*) from processing_jobs where status in ('Queued','Running');
   select count(*) from integration_outbox_events where status in ('Pending','Leased');
   select count(*) from billing_checkout_operations where status in ('Creating','Ready','NeedsReconcile');
   ```
3. Mở App Service cũ, vào *Settings → Environment variables*, chọn *Advanced edit* rồi chép toàn bộ JSON ra file cục bộ. File này chứa secret: **không commit, không gửi qua chat**.

## 1. Redis ở Japan East

- Tạo **Azure Managed Redis** ở Japan East, cùng tier với `fet3d-redis-streams-dev`.
- Bật TLS, cổng `10000`, chính sách eviction `NoEviction`.
- Lấy host mới (`<tên>.japaneast.redis.azure.net`) và access key.

## 2. App Service ở Japan East

1. Tạo App Service plan Linux ở Japan East, cùng SKU với plan cũ.
2. Tạo Web App kiểu container trên plan đó, ví dụ `fire3d-api-jpe`:
   - image `ghcr.io/capstone-fa26se186-fire3d/fire3d-api:<sha đang chạy>`;
   - khai báo thông tin đăng nhập registry giống app cũ.
3. Dán JSON đã chép ở bước 0 vào *Advanced edit*, rồi sửa:
   - `Redis__Host` và `Redis__Password` trỏ sang Redis mới;
   - giữ nguyên mọi connection string Supabase.
4. Bật các cấu hình sau:
   - *Always On*, vì các worker nền (PayOS, email, nhắc hạn, processing) chạy trong process;
   - *HTTPS Only*;
   - *Health check* với đường dẫn `/health/ready`.
5. Nên thêm `Minimum Pool Size=2` vào các connection string, để sau lúc rảnh không phải bắt tay lại với DB.

## 3. GitHub

Từ commit có file này, workflow `azure-deploy.yml` đọc đích deploy từ biến của repository. Khi chưa đặt biến, nó deploy về app East Asia như cũ.

| Loại | Tên | Giá trị |
|---|---|---|
| Secret | `AZURE_WEBAPP_PUBLISH_PROFILE` | Publish profile của app mới (*Overview → Download publish profile*) |
| Variable | `AZURE_WEBAPP_NAME` | Tên app mới, ví dụ `fire3d-api-jpe` |
| Variable | `AZURE_WEBAPP_URL` | Host mặc định của app mới (`https://fire3d-api-jpe-….japaneast-01.azurewebsites.net`), dùng tới khi đổi DNS |

Đặt xong thì chạy lại workflow (*Actions → Build and Deploy to Azure → Run workflow* trên `main`). Bước kiểm tra phiên bản và bước smoke test chạy trên host mới.

## 4. Thử trên host mới, trước khi đổi DNS

- `GET /health/ready` trả 200; `GET /health/version` trả đúng commit.
- Đăng nhập, xem danh sách công trình, trang Phiên bản (gọi thẳng host mới hoặc dùng FE preview trỏ tới host này).
- Đo lại bảng thời gian trong BE#168 (5 lần mỗi endpoint, lấy trung vị) để so trước và sau:
  ```bash
  curl -s -o /dev/null -w '%{time_total}\n' -H "Authorization: Bearer $TOKEN" "$HOST/api/buildings?page=1&pageSize=20"
  ```

Trong lúc này hai app cùng chạy trên một DB. Không có job nào chạy hai lần vì mọi worker giữ lease trong PostgreSQL. Mỗi app có Redis riêng, nên job do app nào phát thì app đó giao.

## 5. Chuyển domain

1. Trên app mới: *Custom domains → Add custom domain*, nhập `api.fet3d.io.vn`. Azure yêu cầu thêm bản ghi TXT `asuid.api`; thêm ở DNS rồi bấm xác minh.
2. Tạo *App Service Managed Certificate* cho domain và gắn SNI SSL.
3. Đổi CNAME `api` sang host mặc định của app mới.
4. Đợi hết TTL, rồi kiểm tra:
   - `nslookup api.fet3d.io.vn` ra host `japaneast`;
   - `https://api.fet3d.io.vn/health/version` trả 200.
5. Đặt biến `AZURE_WEBAPP_URL=https://api.fet3d.io.vn`.

## 6. Dọn dẹp

- Sau khi log của app cũ không còn request mới (khoảng 1 giờ), **Stop** app East Asia, để chỉ còn một bộ worker nền chạy.
- Giữ app cũ, plan cũ và Redis cũ ở trạng thái dừng 3–7 ngày để có đường lui, rồi mới xóa (plan vẫn tính tiền khi app dừng).
- FE: đổi vùng chạy hàm Vercel từ `hkg1` sang `hnd1`.
- Sửa các tài liệu còn ghi host cũ: `production-acceptance.md`, `payos-deployment.md`, `redis-processing.md`.

## Quay lại nếu có sự cố

1. **Start** app East Asia.
2. Trỏ CNAME `api` về `fire3d-api-h3a5h2fgdvajfbcz.eastasia-01.azurewebsites.net`.
3. Đặt lại secret publish profile cũ, rồi xóa hai biến `AZURE_WEBAPP_NAME` và `AZURE_WEBAPP_URL`.

DB không đổi ở bất kỳ bước nào, nên quay lại không mất dữ liệu.
