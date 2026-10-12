using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Fire3D.API.Extensions;

/// <summary>Aligns request/response schemas with what the handlers enforce when the C# shape alone cannot express it.</summary>
public sealed class ContractSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(InitiateIfcUploadRequest) && schema.Properties is not null)
        {
            // The property stays nullable in C# so a missing hash reaches the handler's VALIDATION_ERROR (errors.sha256Hash).
            schema.Required ??= new HashSet<string>();
            schema.Required.Add("sha256Hash");
            schema.Properties["sha256Hash"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String, Pattern = "^[0-9a-fA-F]{64}$", MinLength = 64, MaxLength = 64,
                Description = "Required SHA-256 of the IFC bytes (64 hex characters). Missing or malformed returns 400 VALIDATION_ERROR with errors.sha256Hash."
            };
        }
        return Task.CompletedTask;
    }
}
