using Fire3D.API.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Fire3D.API.Extensions;

public sealed class AuthOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        if (context.Description.ActionDescriptor is not ControllerActionDescriptor action) return Task.CompletedTask;
        var controller = action.ControllerTypeInfo.AsType();
        if (controller != typeof(AuthController) && controller != typeof(AvatarController) && controller != typeof(OrganizationProfileController))
            return Task.CompletedTask;

        foreach (var parameter in operation.Parameters ?? [])
            if (parameter is OpenApiParameter header && header.In == ParameterLocation.Header)
            {
                if (header.Name == "If-Match")
                {
                    header.Required = true;
                    header.Description = "Quoted ETag from the relevant GET profile. Missing: 428; malformed: 400; stale: 412. Complete replay uses the original ETag.";
                }
                else if (header.Name == "X-Installation-Key")
                {
                    header.Required = true;
                    header.Description = "Client-generated 32 random bytes encoded as base64url. Keep with the installation UUID in secure storage; UUID alone is not ownership proof.";
                }
            }

        if (controller == typeof(AvatarController) && action.MethodInfo.Name == nameof(AvatarController.Upload))
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Required = new HashSet<string> { "file" },
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary",
                                    Description = "JPEG/PNG/WebP; max 5 MiB, 4096×4096 pixels, one frame. Use a file picker, not an avatar URL." }
                            }
                        }
                    }
                }
            };
        }
        return Task.CompletedTask;
    }
}
