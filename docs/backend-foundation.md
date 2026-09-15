# Nền tảng BE — cấu trúc và module

## Kiến trúc

Chốt hướng triển khai: Clean Architecture, một ứng dụng BE, tổ chức use case theo module và MediatR command/query. Đây là đề xuất kỹ thuật đã triển khai của BE để team review, chưa thay thế việc leader xác nhận checklist.

| Project | Trách nhiệm | Project reference hiện có |
|---|---|---|
| Fire3D.Domain | Entity và enum nghiệp vụ | Không phụ thuộc project khác |
| Fire3D.Application | Use case, DTO, interface, handler MediatR | Domain |
| Fire3D.Infrastructure | EF Core/PostgreSQL, store, hashing, JWT | Application, Domain |
| Fire3D.API | HTTP controller, auth policy, OpenAPI, DI và khởi động | Application, Infrastructure |
| Fire3D.AuthTests | Kiểm thử auth/admin, gồm integration PostgreSQL | Test project riêng trong solution |

Solution: Fire3D/Fire3D.slnx. Các thư mục backend/Src và backend/Tests trong solution là nhóm hiển thị của IDE; project thực tế nằm trực tiếp dưới Fire3D/. DI giữ ở API/Extensions. Controller chuyển yêu cầu tới Application; Infrastructure triển khai interface do Application định nghĩa. Application/Domain không phụ thuộc API hoặc Infrastructure.

## Ranh giới module

| Module | Vị trí/trách nhiệm | Tiến độ |
|---|---|---|
| Authentication | Application/Authentication; đăng nhập, refresh, logout, tài khoản hiện tại, cấp tài khoản, bootstrap admin | Đã có code |
| Administration | Application/Administration; quản trị tổ chức và tài khoản | Đã có code |
| Building / IFC | Tòa nhà, revision, upload/xử lý IFC và artifact | Có entity/mapping và spike parse/geometry riêng; chưa có use case/API |
| Scenario / Release | Kịch bản, phiên bản, kiểm tra, phát hành | Có entity/mapping; chưa có use case/API |
| Training | Huấn luyện, session, sự kiện, kết quả | Có entity/mapping; chưa có use case/API |

Danh sách module nghiệp vụ là định hướng chia việc, không phải cam kết đã hoàn thành. Entity khác tồn tại trong schema không tự trở thành feature trong phạm vi Week 2. Chỉ thêm folder/use case khi triển khai; chưa cần tách microservice hoặc tạo project cho từng module.

## Checklist môi trường Week 2

Đầu ra đủ cho task restore/build/run: log restore thành công, build thành công và bằng chứng ứng dụng khởi động + GET Swagger/OpenAPI thành công. Không bắt buộc vừa log vừa ảnh. Ghi rõ commit, lệnh, môi trường và kết quả; chỉ thấy trang Swagger chưa chứng minh kết nối DB, login hay xử lý IFC hoạt động.

Lệnh từ gốc BE:

```powershell
dotnet restore Fire3D/Fire3D.slnx
dotnet build Fire3D/Fire3D.slnx --no-restore
dotnet run --project Fire3D/Fire3D.API/Fire3D.API.csproj --no-build --launch-profile http
```

Cần .NET 10 SDK, connection string và JWT local hợp lệ theo authentication.md. Kiểm tra /swagger/index.html và /openapi/v1.json. Không đưa secret vào log/ảnh/PR. Build/run không yêu cầu tự chạy migration hoặc ghi dữ liệu vào database ứng dụng.

Quyền PlatformAdmin: xem [platform-admin-policy.md](platform-admin-policy.md). Phần auth/admin hiện có: [authentication.md](authentication.md), [administration.md](administration.md), [testing-authorization.md](testing-authorization.md).

Week 2: [nghiệm thu và checklist review](week2-review.md); [IFC capability spike](../tools/ifc-spike/README.md).
