# Đo và tối ưu authentication

Login phát metric `fet3d.auth.login.phase.duration` (ms) trên meter `FET3D.Authentication`, chỉ có nhãn phase hữu hạn: Total, Lookup, PasswordVerification, LockWait, Revalidation, Persistence. Total bao gồm các phase nên không cộng chúng vào Total lần nữa. Revalidation đo đọc lại user; phần kiểm lifecycle organization nằm trong Total, chưa có nhãn riêng.

Có thể bật `AuthDiagnostics__DatabaseTimingEnabled=true` để nhận `fet3d.db.command.duration` trên meter `FET3D.Database` và log Debug ở `Fire3D.Infrastructure.Authentication.AuthDatabaseDiagnostics`. Interceptor đo EF command async, chỉ ghi loại reader/scalar/nonquery và duration; không ghi SQL, parameter, email, user ID, hash, token. Không tính Npgsql command trực tiếp hoặc thời gian mở kết nối/commit là EF command. Thu thập p50/p95/p99 theo môi trường và traffic, tắt Debug sau khi chẩn đoán. Meter cần collector/listener của hệ thống monitoring; thêm meter không tự cấu hình Azure exporter.

Phân biệt chậm network/pool, thời gian chờ khóa, PBKDF2 và thực thi SQL. Truy vấn với bảng ít dòng dùng SeqScan không đủ lý do thêm index. Không giảm độ mạnh password hash, bỏ reset fencing, cache quyền/tenant/family hoặc dùng kết nối migration cho API để tối ưu.

Tại snapshot kiểm tra, Supabase cùng phiên có RTT SELECT 1 khoảng 107–121 ms còn SQL lookup/family/reset fence dưới 1 ms; đây là phép đo kết nối từ máy local, không phải latency login Azure. Cần đo App Service thực tế trước khi quyết định region/pool. Tránh tạo pool mới mỗi request; giữ pooling và giới hạn phù hợp connection budget, không tự tăng max pool theo phỏng đoán.
