using Fire3D.API.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Fire3D.API.Extensions;

public sealed class BillingOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation,OpenApiOperationTransformerContext context,CancellationToken ct)
    {
        if(context.Description.ActionDescriptor is ControllerActionDescriptor action
            && action.ControllerTypeInfo.AsType()==typeof(BillingController))
            foreach(var parameter in operation.Parameters ?? [])
                if(parameter is OpenApiParameter header && header.In==ParameterLocation.Header
                    && header.Name is "If-Match" or "Idempotency-Key")
                    header.Required=true;
        return Task.CompletedTask;
    }
}
