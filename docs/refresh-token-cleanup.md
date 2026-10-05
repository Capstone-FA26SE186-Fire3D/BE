# Dọn refresh token

`RefreshTokenCleanupWorker` tiếp tục chạy theo `AuthTokenCleanup` (mặc định 60 phút, retention 7 ngày, batch 500). Family chỉ được xóa khi **mọi** token đã hết hạn qua retention; consumed/revoked không đủ để cho phép xóa sớm. JWT access token không được lưu thành bảng để dọn.

Migration `AddRefreshCleanupGate` bổ sung `cleanup_expired_refresh_family(user_id,family_id,retention_days)`. API có quyền đọc và gọi gate, không được cấp DELETE trực tiếp. NOLOGIN maintenance owner chỉ có SELECT/DELETE trên refresh tokens. Gate lấy khóa lifecycle chung rồi khóa user, kiểm tra lại family bằng thời gian hiện tại sau khi chờ khóa và xóa atomic. Anonymous/Supabase authenticated không được gọi gate.

Áp migration trước khi deploy store mới. Không đổi family/session contract, password hash hoặc retention hiện có. Lưu batch theo `max(expires_at), user_id, family_id`; một statement/gate cho mỗi family được chọn.

Kiểm chứng PostgreSQL cô lập: restricted API chạy được khi không có direct DELETE; giữ family mixed/active/recent; biên retention; thứ tự batch; thêm token còn hiệu lực trong khi cleanup chờ khóa. Fixture reset schema rút gọn dùng SQL migration gate thực tế; test quyền dùng EF baseline. Kiểm tra toàn bộ migration chain được báo riêng, không suy production từ các fixture này.
