using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Fire3D.API.Extensions;

public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info.Description = "Fire3D BE — Clean Architecture: API, Application, Domain, Infrastructure. "
            + "PlatformAdmin được quản trị xuyên tổ chức và xem thông tin cá nhân phục vụ quản trị. "
            + "Hiện đã triển khai quản trị tổ chức/tài khoản. Quyền thao tác thay tổ chức đối với tòa nhà, IFC, "
            + "kịch bản và huấn luyện đã được chốt ở mức yêu cầu; các API nghiệp vụ này chưa triển khai. "
            + "Mô tả Swagger không thay thế kiểm tra quyền phía server. Xem docs/platform-admin-policy.md.";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
            Description = "Paste the access token returned by /api/auth/login."
        };
        foreach (var (path, item) in document.Paths)
        {
            if (path is "/api/auth/login" or "/api/auth/refresh") continue;
            if (item.Operations is null) continue;
            foreach (var operation in item.Operations.Values)
            {
                if (path.StartsWith("/api/accounts", StringComparison.Ordinal)
                    || path.StartsWith("/api/organizations", StringComparison.Ordinal))
                {
                    operation.Description = "Chỉ PlatformAdmin. Quản trị xuyên tổ chức bằng danh tính admin hiện tại; "
                        + "không đăng nhập giả làm người dùng. Thiếu/sai token: 401; vai trò khác: 403. "
                        + (path.StartsWith("/api/accounts", StringComparison.Ordinal)
                            ? "Thông tin trả về gồm ID, email, họ tên, vai trò, tổ chức và trạng thái; "
                                + "API quản lý còn có thời điểm đăng nhập/tạo/cập nhật. Không trả mật khẩu, hash hoặc token. "
                                + "Tạo tài khoản và đổi trạng thái có audit khi phát sinh thay đổi. Không đổi role/organization của tài khoản đã tạo. "
                                + "Không được tự vô hiệu hóa tài khoản của mình."
                            : "Hỗ trợ tạo, danh sách, chi tiết và kích hoạt/vô hiệu hóa tổ chức. "
                                + "Thao tác ghi có audit khi phát sinh thay đổi, ghi nhận admin thực hiện và tổ chức đích. "
                                + "Vô hiệu hóa tổ chức thu hồi phiên của OrganizationUser; kích hoạt lại không phục hồi token cũ.");
                }
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = []
                });
            }
        }
        return Task.CompletedTask;
    }
}
