using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Fire3D.API.Extensions;

public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
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
