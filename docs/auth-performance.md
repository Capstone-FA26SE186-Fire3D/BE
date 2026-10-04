# Đo và tối ưu authentication

Login phát metric `fet3d.auth.login.phase.duration` (ms) trên meter `FET3D.Authentication`, chỉ có nhãn phase hữu hạn: Total, Lookup, PasswordVerification, LockWait, Revalidation, Persistence. Total bao gồm các phase nên không cộng chúng vào Total lần nữa. Revalidation đo đọc lại user; phần kiểm lifecycle organization nằm trong Total, chưa có nhãn riêng.

Có thể bật `AuthDiagnostics__DatabaseTimingEnabled=true` để nhận `fet3d.db.command.duration` trên meter `FET3D.Database` và log Debug ở `Fire3D.Infrastructure.Authentication.AuthDatabaseDiagnostics`. Interceptor đo EF command async, chỉ ghi loại reader/scalar/nonquery và duration; không ghi SQL, parameter, email, user ID, hash, token. Không tính Npgsql command trực tiếp hoặc thời gian mở kết nối/commit là EF command. Thu thập p50/p95/p99 theo môi trường và traffic, tắt Debug sau khi chẩn đoán. Meter cần collector/listener của hệ thống monitoring; thêm meter không tự cấu hình Azure exporter.

Phân biệt chậm network/pool, thời gian chờ khóa, PBKDF2 và thực thi SQL. Truy vấn với bảng ít dòng dùng SeqScan không đủ lý do thêm index. Không giảm độ mạnh password hash, bỏ reset fencing, cache quyền/tenant/family hoặc dùng kết nối migration cho API để tối ưu.

Tại snapshot kiểm tra, Supabase cùng phiên có RTT SELECT 1 khoảng 107–121 ms còn SQL lookup/family/reset fence dưới 1 ms; đây là phép đo kết nối từ máy local, không phải latency login Azure. Cần đo App Service thực tế trước khi quyết định region/pool. Tránh tạo pool mới mỗi request; giữ pooling và giới hạn phù hợp connection budget, không tự tăng max pool theo phỏng đoán.

Login xác thực PBKDF2/rehash trước khi lấy khóa database. Dưới khóa lifecycle rồi user, đọc lại identity, so sánh đúng email/hash đã xác thực và kiểm lifecycle/email verification. Hash đã đổi thì trả INVALID_CREDENTIALS; không dùng kết quả xác thực cũ để cấp phiên. Password reset Pending/finished fence tiếp tục được kiểm trước khi ghi session.

Hai advisory locks được gửi trong một round trip theo đúng thứ tự. Một CTE parameterized cập nhật login/rehash, ghi refresh token và audit cùng transaction, có reset fence. Lỗi audit rollback cả login và token. Không có transaction mở khi chạy PBKDF2, không đổi hash algorithm/iteration, không cache quyền. Phải deploy binary chứa các method store/handler đồng bộ; API request/response không đổi.

Unit tests kiểm verify trước khóa, wrong password không lấy khóa và identity thay đổi. PostgreSQL/HTTP test lấy khóa từ connection khác, thay mật khẩu hoặc khóa account, rồi kiểm login bị từ chối; lỗi audit không để lại session. Full-migration auth fixture kiểm chain thực tế; test dữ liệu legacy tạo database ở migration lịch sử riêng, không downgrade hardening. Kết quả test không phải benchmark production.

Sau xác minh JWT, middleware kiểm user/role/tenant, email verification, organization lifecycle và active refresh family trong một EXISTS query. Không cache; revoke/disable tiếp tục có hiệu lực trên request kế tiếp. Claim role phải là tên enum đúng, tenant GUID canonical đúng như token do server cấp. Test PostgreSQL chứng minh OrganizationUser validation giảm từ ba EF queries xuống một và vẫn từ chối sai tenant/family, account/organization khóa, soft-delete, pending email, token consumed/revoked/expired.
