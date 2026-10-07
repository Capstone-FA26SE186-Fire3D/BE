using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
 [PostgresFact]
 public async Task Selected_OpenAPI_headers_roles_DTOs_and_HTTP_authorization_match()
 {
  using var doc=JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));var root=doc.RootElement;var paths=root.GetProperty("paths");
  foreach(var (path,method,header) in new[]{
   ("/api/releases","post","Idempotency-Key"),
   ("/api/buildings/{id}/access","patch","If-Match"),
   ("/api/buildings/{id}/participation-code/rotate","post","If-Match"),
   ("/api/buildings/{id}/participation-code","delete","If-Match"),
   ("/api/feedback","post","Idempotency-Key"),
   ("/api/support/tickets","post","Idempotency-Key"),
   ("/api/support/tickets/{id}/messages","post","Idempotency-Key"),
   ("/api/admin/support/tickets/{id}","patch","If-Match"),
   ("/api/admin/feedback/{id}/status","patch","If-Match")})
  {
   var operation=paths.GetProperty(path).GetProperty(method);Assert.Contains(operation.GetProperty("parameters").EnumerateArray(),p=>p.GetProperty("name").GetString()==header&&p.TryGetProperty("required",out var required)&&required.GetBoolean());
   Assert.True(operation.GetProperty("security").GetArrayLength()>0);
  }
  foreach(var (path,schema) in new[]{("/api/payments/payos/checkouts/{id}","PayosCheckoutResponse"),("/api/payments/payos/requests/{id}","PayosPaymentResponse")})
  {
   var operation=paths.GetProperty(path).GetProperty("get");Assert.Contains(schema,operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetRawText());
   Assert.Contains("OrganizationUser",operation.GetProperty("description").GetString());Assert.Contains("PlatformAdmin",operation.GetProperty("description").GetString());
  }
  Assert.Equal("/",root.GetProperty("servers")[0].GetProperty("url").GetString());
  Assert.True(!paths.GetProperty("/api/auth/registration/request-otp").GetProperty("post").TryGetProperty("security",out var security)||security.GetArrayLength()==0);
  foreach(var path in new[]{"/api/feedback","/api/support/tickets","/api/buildings/"+Guid.NewGuid()+"/trainings"})
   Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path)).StatusCode);
  var output=Environment.GetEnvironmentVariable("FIRE3D_OPENAPI_OUTPUT");if(output is not null)await File.WriteAllTextAsync(output,root.GetRawText());
 }
}
