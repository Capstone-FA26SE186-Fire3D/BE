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
        var controller=action.ControllerTypeInfo.AsType();
        var tagged=controller==typeof(Controllers.BillingController) && type.Name is "PackageResponse" or "DiscountResponse" or "QuotationResponse"
            || controller==typeof(Controllers.OrganizationProfileController)
            || controller==typeof(Controllers.AuthController) && action.MethodInfo.Name is "Me" or "UpdateMe";
        if(tagged)
            foreach(var response in operation.Responses.Where(x=>int.TryParse(x.Key,out var status)&&status is >=200 and <300).Select(x=>x.Value).OfType<OpenApiResponse>())
            {
                response.Headers??=new Dictionary<string,IOpenApiHeader>();
                response.Headers["ETag"]=new OpenApiHeader{Description="Current quoted representation revision. Send unchanged as If-Match for updates.",Schema=new OpenApiSchema{Type=JsonSchemaType.String}};
            }
        if(operation.Responses.TryGetValue("429",out var rejected)&&rejected is OpenApiResponse error)
        {
            error.Headers??=new Dictionary<string,IOpenApiHeader>();
            error.Headers["Retry-After"]=new OpenApiHeader{Description="Wait this many seconds before retrying.",Schema=new OpenApiSchema{Type=JsonSchemaType.Integer}};
        }
    }
}
