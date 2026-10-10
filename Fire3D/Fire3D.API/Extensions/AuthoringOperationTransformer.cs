using Fire3D.API.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Reflection;
namespace Fire3D.API.Extensions;
public sealed class AuthoringOperationTransformer:IOpenApiOperationTransformer
{
 public Task TransformAsync(OpenApiOperation operation,OpenApiOperationTransformerContext context,CancellationToken ct)
 {
  if(context.Description.ActionDescriptor is not ControllerActionDescriptor action)return Task.CompletedTask;
  var required=action.MethodInfo.GetParameters().Where(p=>p.GetCustomAttribute<RequiredRequestHeaderAttribute>() is not null)
   .Select(p=>p.GetCustomAttribute<FromHeaderAttribute>()?.Name ?? p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
  foreach(var parameter in operation.Parameters ?? [])
    if(parameter is OpenApiParameter header && header.In==ParameterLocation.Header && required.Contains(header.Name)){
     header.Required=true;
     header.Description=header.Name switch {
      "If-Match"=>"Required current quoted resource ETag; missing 428, malformed 400, stale 412. Replay uses the original precondition.",
      "Idempotency-Key"=>"Required; replay semantics are endpoint-specific. Same actor/key with different canonical input returns 409.",
      "X-Installation-Key"=>"Required installation ownership proof: 32 random bytes encoded as base64url, retained in secure storage with the UUID.",
      _=>header.Description};
    }
  foreach(var metadata in action.MethodInfo.GetCustomAttributes<ResponseHeaderAttribute>())
   foreach(var response in (operation.Responses ?? new OpenApiResponses()).Where(r=>int.TryParse(r.Key,out var status)&&status is >=200 and <300).Select(r=>r.Value).OfType<OpenApiResponse>()){
    response.Headers??=new Dictionary<string,IOpenApiHeader>();
    response.Headers[metadata.Name]=new OpenApiHeader{Schema=new OpenApiSchema{Type=JsonSchemaType.String},Description=metadata.Name=="ETag"?"Current quoted representation revision; send unchanged as If-Match for updates.":null};
   }
  return Task.CompletedTask;
 }
}
