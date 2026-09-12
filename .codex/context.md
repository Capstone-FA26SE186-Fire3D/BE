# Context — BE

## Hiện trạng đã kiểm tra

- [.NET 10 solution](../Fire3D/Fire3D.slnx) gồm API, Application, Domain và Infrastructure.
- [Program.cs](../Fire3D/Fire3D.API/Program.cs) đăng ký database, controllers, OpenAPI/Swagger cho development; không suy ra mọi nghiệp vụ đã có chỉ vì entities tồn tại.
- [Database DI](../Fire3D/Fire3D.API/Extensions/DependencyInjection.cs) dùng EF Core/Npgsql và yêu cầu `ConnectionStrings:DefaultConnection`.
- [Domain enums](../Fire3D/Fire3D.Domain/Enums/DatabaseEnums.cs) và mapping PostgreSQL cần khớp tên/nhãn SQL. DI dùng `NpgsqlNullNameTranslator` để giữ chữ hoa/thường.
- Persistence ở [DbContext](../Fire3D/Fire3D.Infrastructure/Persistence/Fire3DDbContext.cs). Không chạy migration hoặc đổi schema database ngoài phạm vi được yêu cầu.
- Không lưu connection string/credential vào context hoặc handoff. Không track .vs, bin, obj.

## Chạy và kiểm tra

Từ gốc BE, với .NET 10 SDK/phụ thuộc sẵn sàng:
- `dotnet build Fire3D/Fire3D.slnx`: kiểm tra biên dịch, không thay thế integration test.
- `dotnet run --project Fire3D/Fire3D.API/Fire3D.API.csproj`: chạy API khi task cần và đã có cấu hình database phù hợp.
- Chưa thấy test project trong solution đã kiểm tra; không dùng kết quả `dotnet test` không chạy test nào để kết luận nghiệp vụ đạt.
- Với thay đổi database/enum, cần kiểm tra có mục tiêu trên database test được phép dùng; không tự kết nối production.
- Ghi bằng chứng build/test của từng task trong handoff local hoặc PR; context này không phải báo cáo kiểm thử.

## Thiết kế liên quan

Khi có Docs bên cạnh, đối chiếu `fire_evacuation_schema.sql`, `fire_evacuation_erd.md`, requirements và workflows theo task. Docs giữ ba vai trò PlatformAdmin, OrganizationUser, Trainee; readiness nội bộ không phải phê duyệt PCCC. Nếu clone độc lập thiếu tài liệu bắt buộc, báo thiếu thay vì đoán schema hoặc tự clone.
