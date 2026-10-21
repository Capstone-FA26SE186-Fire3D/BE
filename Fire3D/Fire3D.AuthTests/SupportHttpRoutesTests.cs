using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
 [PostgresFact]
 public async Task Support_HTTP_lists_feedback_and_tickets_and_both_sides_reply_on_a_ticket()
 {
  var owner=await SeedSupportUser();var email=(string)(await ScalarAsync($"SELECT email FROM users WHERE id='{owner}'"))!;
  using var trainee=factory!.CreateClient();trainee.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync(email)).AccessToken);
  using var admin=factory.CreateClient();admin.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
  static HttpRequestMessage Post(string url,object body,string? key){var r=new HttpRequestMessage(HttpMethod.Post,url){Content=JsonContent.Create(body)};if(key is not null)r.Headers.Add("Idempotency-Key",key);return r;}
  static async Task<JsonElement> Json(HttpResponseMessage r){var text=await r.Content.ReadAsStringAsync();Assert.True(r.IsSuccessStatusCode,text);return JsonDocument.Parse(text).RootElement.Clone();}
  var feedback=await trainee.SendAsync(Post("/api/feedback",new{category="Bug",message="The map froze",rating=4},"feedback"));
  Assert.Equal(HttpStatusCode.Created,feedback.StatusCode);Assert.Equal("/api/feedback",feedback.Headers.Location!.OriginalString);
  var mine=await Json(await trainee.GetAsync("/api/feedback?pageSize=5"));
  Assert.Equal(1,mine.GetProperty("total").GetInt32());Assert.Equal("The map froze",mine.GetProperty("items")[0].GetProperty("message").GetString());
  var ticket=await Json(await trainee.SendAsync(Post("/api/support/tickets",new{subject="Frozen map",description="It stops at floor 2"},"ticket")));var id=ticket.GetProperty("id").GetGuid();
  Assert.Equal(HttpStatusCode.BadRequest,(await trainee.SendAsync(Post($"/api/support/tickets/{id}/messages",new{message="No key"},null))).StatusCode);
  var reply=await trainee.SendAsync(Post($"/api/support/tickets/{id}/messages",new{message="It happens every time"},"message"));
  Assert.Equal(HttpStatusCode.Created,reply.StatusCode);Assert.Equal($"/api/support/tickets/{id}",reply.Headers.Location!.OriginalString);
  Assert.Equal("It happens every time",(await Json(reply)).GetProperty("message").GetString());
  var open=await Json(await trainee.GetAsync("/api/support/tickets?status=Open"));
  Assert.Equal(1,open.GetProperty("total").GetInt32());Assert.Equal(id,open.GetProperty("items")[0].GetProperty("id").GetGuid());
  Assert.Equal(0,(await Json(await trainee.GetAsync("/api/support/tickets?status=Closed"))).GetProperty("total").GetInt32());

  var queue=await Json(await admin.GetAsync("/api/admin/feedback?status=Submitted"));
  Assert.Equal(1,queue.GetProperty("total").GetInt32());
  Assert.Equal(1,(await Json(await admin.GetAsync("/api/admin/support/tickets?status=Open&pageSize=10"))).GetProperty("total").GetInt32());
  var detail=await admin.GetAsync($"/api/admin/support/tickets/{id}?pageSize=10");
  Assert.Equal("\"support-2\"",detail.Headers.ETag!.ToString());
  Assert.Equal(1,(await Json(detail)).GetProperty("messages").GetProperty("total").GetInt32());
  var answer=await admin.SendAsync(Post($"/api/admin/support/tickets/{id}/messages",new{message="We are looking into it"},"admin-message"));
  Assert.Equal(HttpStatusCode.Created,answer.StatusCode);Assert.Equal($"/api/support/tickets/{id}",answer.Headers.Location!.OriginalString);
  Assert.Equal(adminId,(await Json(answer)).GetProperty("authorId").GetGuid());
  Assert.Equal(HttpStatusCode.NotFound,(await admin.SendAsync(Post($"/api/admin/support/tickets/{Guid.NewGuid()}/messages",new{message="Lost"},"admin-missing"))).StatusCode);
  var thread=await Json(await trainee.GetAsync($"/api/support/tickets/{id}"));
  Assert.Equal(2,thread.GetProperty("messages").GetProperty("total").GetInt32());Assert.Equal(3,thread.GetProperty("revision").GetInt64());
  Assert.Equal(HttpStatusCode.Forbidden,(await trainee.GetAsync("/api/admin/feedback")).StatusCode);
 }
}
