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
