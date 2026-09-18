# Week 2 — đầu ra, nghiệm thu và bàn giao review

Nhóm GFA26SE133; đề tài FA26SE186; BE phụ trách Nguyễn Hoàng Nam. PR review: https://github.com/Capstone-FA26SE186-Fire3D/BE/pull/2 (target develop). Kết quả dưới đây kiểm tra ngày 2026-09-15; chưa có review độc lập của thành viên khác.

## Tiêu chí và bằng chứng

| Task | Tiêu chí | Kết quả |
|---|---|---|
| Nền tảng BE | Solution chia API/Application/Domain/Infrastructure; dependency đúng chiều | Đã kiểm tra project references; xem backend-foundation.md |
| Restore/build/run | Restore/build thành công, HTTP Swagger/OpenAPI 200 | Đạt; solution build đã ghi nhận 0 warning/error |
| JWT local | Đọc appsettings + User Secrets, không cần JWT environment tạm | Đạt: Fire3D.API / Fire3D.Clients, access 3600 giây, refresh config 7 ngày |
| Database auth local | Bảng refresh token và admin ban đầu tồn tại | Đạt: áp dụng riêng database/001_auth_refresh_tokens.sql trên localhost; bootstrap qua Application handler |
| Auth smoke trên DB local | Login/me/refresh/logout và thu hồi phiên | 200/200/200/204, GET me sau logout trả 401 |
| Auth/admin regression | Bộ integration trên PostgreSQL tạm, không bỏ qua test | 22 passed, 0 failed, 0 skipped; không dùng application DB làm fixture |
| Quyền/tenant | API + Application check role; kiểm tra role/org binding và trạng thái | Bộ test auth/admin đạt; ma trận chi tiết ở testing-authorization.md |
| Quyền PlatformAdmin | Xác định quyền đặc biệt và giới hạn triển khai | platform-admin-policy.md và 8 mô tả Swagger đã có; cần team review |
| Khả năng IFC ban đầu | Đọc IFC mẫu, tầng/đơn vị, tạo hình học và báo lỗi | Spike IFC4: 7 test đạt; 2 tầng, 2 tường, 2 mesh; tools/ifc-spike/README.md |
| Rà soát chéo | Có người review và kết luận độc lập | Chưa hoàn tất; không tự đánh dấu thay thành viên khác |
| Jira | Issue đúng task có link PR/bằng chứng/blocker | Đã chuẩn bị nội dung dưới đây; chưa cập nhật Jira do chưa có mã issue/kết nối Jira |

## Lệnh kiểm tra tái lập

```powershell
dotnet restore Fire3D/Fire3D.slnx
dotnet build Fire3D/Fire3D.slnx --no-restore --nologo
$env:FIRE3D_TEST_USE_LOCAL_SECRETS = '1'
dotnet test Fire3D/Fire3D.AuthTests/Fire3D.AuthTests.csproj --no-build --no-restore
Remove-Item Env:FIRE3D_TEST_USE_LOCAL_SECRETS
& .codex/local/ifc-venv/Scripts/python.exe -m unittest discover -s tools/ifc-spike -p 'test_*.py' -v
```

Bộ auth test yêu cầu PostgreSQL localhost cho phép tạo DB tạm và sibling Docs/database/{00_types,10_core,20_functions}. Không chạy lại bootstrap toàn bộ DB ứng dụng. Không gọi một lần chạy không có test/skipped là đạt.

Local đã có JWT SigningKey trong User Secrets. Tài khoản bootstrap local được lưu trong LocalAdmin:Email và LocalAdmin:Password; mở Fire3D.API → Manage User Secrets để lấy thông tin trên máy đã setup, không dán mật khẩu/token lên Jira hoặc PR. Các khóa LocalAdmin chỉ phục vụ lưu thông tin local, không tự tạo admin khi server chạy. Thành viên khác setup theo authentication.md, không dùng chung credential.

## Lỗi đã gặp và cách xử lý

| Hiện tượng | Xử lý | Trạng thái |
|---|---|---|
| Build bị khóa DLL bởi server preview cũ | Dừng đúng tiến trình API local rồi build lại | Đã xử lý; build cuối 0 warning/error |
| Server thiếu JWT hợp lệ | Lưu khóa Base64 ngẫu nhiên trong User Secrets; bỏ override tạm, restart | Đã kiểm tra JWT thật với issuer/audience/lifetime mong muốn |
| DB local thiếu auth_refresh_tokens/admin | Áp dụng migration bổ sung và CLI bootstrap | Đã kiểm tra login/rotation/logout; có audit/session phát sinh từ smoke |

## Checklist dành cho người review

- [ ] Leader/BE reviewer xác nhận Clean Architecture và trách nhiệm từng module.
- [ ] FE/người phụ trách auth xác nhận request/response, Bearer token, refresh serialization, lỗi 401/403 và pagination.
- [ ] Team xác nhận quyền PlatformAdmin xuyên tổ chức, dữ liệu cá nhân được xem và use case hiệu chỉnh kết quả lịch sử.
- [ ] Người phụ trách IFC chạy fixture rồi bổ sung file công trình đại diện; thống nhất output/worker contract trước tích hợp.
- [ ] Reviewer ghi tên, ngày, kết luận và issue lỗi; chưa merge PR trước khi review.

## Nội dung sẵn để cập nhật Jira

Task: W02 — Thiết kế nền tảng BE, xác thực và khả năng xử lý IFC.

Đầu ra: PR #2 vào develop; docs/backend-foundation.md; docs/platform-admin-policy.md; docs/testing-authorization.md; tools/ifc-spike/README.md và samples/inspection.json.

Kiểm tra: restore/build đạt; auth/admin 22/22; auth smoke local đạt (JWT Fire3D, 60 phút); Swagger/OpenAPI 200; IFC spike 7/7 và 2 mesh từ fixture IFC4 tự tạo.

Đề xuất trạng thái: Ready for review cho phần triển khai/kiểm tra; task rà soát chéo vẫn chờ reviewer. Chưa đổi trạng thái trên Jira.

Còn thiếu: review độc lập, mã issue/kết nối Jira, kiểm thử IFC công trình thật. Spike chưa bao gồm upload/private storage/durable jobs/GLB/graph/Unity. Các module nghiệp vụ tương lai chưa có server-side quyền PlatformAdmin.