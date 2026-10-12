using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
namespace Fire3D.API.Extensions;

// Error attributes at controller level suppress ASP.NET's inferred success response.
// Restore only a concrete declared result type, never guess status for IActionResult.
public sealed class SuccessResponseTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation,OpenApiOperationTransformerContext context,CancellationToken ct)
    {
        if(context.Description.ActionDescriptor is not ControllerActionDescriptor action)return;
        var type=action.MethodInfo.ReturnType;
        if(type.IsGenericType && (type.GetGenericTypeDefinition()==typeof(Task<>)||type.GetGenericTypeDefinition()==typeof(ValueTask<>)))type=type.GetGenericArguments()[0];
        if(type.IsGenericType && type.GetGenericTypeDefinition()==typeof(ActionResult<>))type=type.GetGenericArguments()[0];
        operation.Responses??=new OpenApiResponses();
        if(!operation.Responses.Keys.Any(key=>int.TryParse(key,out var status)&&status is >=200 and <300)
            && type!=typeof(void) && type!=typeof(Task) && !typeof(IActionResult).IsAssignableFrom(type))
        {
            var schema=await context.GetOrCreateSchemaAsync(type,null,ct);
            operation.Responses["200"]=new OpenApiResponse{Description="OK",Content=new Dictionary<string,OpenApiMediaType>{["application/json"]=new(){Schema=schema}}};
        }
        // Rate-limited endpoints document their 429 ProblemDetails; the rejection handler always writes one.
        if(!operation.Responses.ContainsKey("429")&&context.Description.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>().Any())
        {
            var problem=await context.GetOrCreateSchemaAsync(typeof(ProblemDetails),null,ct);
            operation.Responses["429"]=new OpenApiResponse{Description="Rate limit exceeded (code ADMINISTRATION_RATE_LIMITED or AUTH_REFRESH_RATE_LIMITED).",
                Content=new Dictionary<string,OpenApiMediaType>{["application/problem+json"]=new(){Schema=problem}}};
        }
        if(operation.Responses.TryGetValue("429",out var rejected)&&rejected is OpenApiResponse error)
        {
            error.Headers??=new Dictionary<string,IOpenApiHeader>();
            error.Headers["Retry-After"]=new OpenApiHeader{Description="Wait this many seconds before retrying.",Schema=new OpenApiSchema{Type=JsonSchemaType.Integer}};
        }
    }
}
