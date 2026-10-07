using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Npgsql;
using Fire3D.Application.Support;
using Fire3D.Infrastructure.Support;
using Fire3D.Domain.Enums;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
 [PostgresFact]
 public async Task Support_ticket_create_replay_returns_one_ticket_and_receipt()
 {
  var owner=Guid.NewGuid();await ExecuteAsync($"INSERT INTO users(id,email,username,role,is_active,password_hash,email_verified_at,created_at,updated_at) SELECT '{owner}','support-{owner:N}@example.test','s_'||left(md5(gen_random_uuid()::text),20),'Trainee',true,password_hash,now(),now(),now() FROM users WHERE id='{adminId}'");
  var login=await LoginAsync($"support-{owner:N}@example.test");client.DefaultRequestHeaders.Authorization=new("Bearer",login.AccessToken);client.DefaultRequestHeaders.Add("Idempotency-Key","support-first");
  var body=new{subject="Fixture support",description="Need help"};
  var one=await client.PostAsJsonAsync("/api/support/tickets",body);var two=await client.PostAsJsonAsync("/api/support/tickets",body);
  Assert.Equal(HttpStatusCode.Created,one.StatusCode);Assert.Equal(HttpStatusCode.Created,two.StatusCode);
  var a=JsonDocument.Parse(await one.Content.ReadAsStringAsync());var b=JsonDocument.Parse(await two.Content.ReadAsStringAsync());Assert.Equal(a.RootElement.GetProperty("id").GetGuid(),b.RootElement.GetProperty("id").GetGuid());
  Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM support_tickets"));Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM support_command_receipts"));
 }

 private async Task<Guid> SeedSupportUser()
 {
  var owner=Guid.NewGuid();await ExecuteAsync($"INSERT INTO users(id,email,username,role,is_active,password_hash,email_verified_at,created_at,updated_at) SELECT '{owner}','support-{owner:N}@example.test','s_'||left(md5(gen_random_uuid()::text),20),'Trainee',true,password_hash,now(),now(),now() FROM users WHERE id='{adminId}'");return owner;
 }
 private async Task WithSupportRuntime(Func<string,Task> action)
 {
  var login="support_test_"+Guid.NewGuid().ToString("N");await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS;GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION support_command_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO {login}");
  try{await action(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Password="",Pooling=false}.ConnectionString);}finally{await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}");}
 }
 [PostgresFact]
 public async Task Support_receipts_concurrent_replay_scope_validation_and_audit_rollback()
 {
  var owner=await SeedSupportUser();var family=await SeedPlaytestFamily(owner);var stranger=await SeedSupportUser();var sf=await SeedPlaytestFamily(stranger);
  await WithSupportRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);await using var db2=BuildingContext(runtime);var service=new SupportService(db);var other=new SupportService(db2);
   var input=new CreateTicketRequest("Fixture","Description");var results=await Task.WhenAll(service.Execute("CreateTicket",owner,family,null,input,"create",null,default),other.Execute("CreateTicket",owner,family,null,input,"create",null,default));
   Assert.All(results,x=>Assert.True(x.IsSuccess,x.Error?.Code));var id=results[0].Value.GetProperty("id").GetGuid();Assert.Equal(id,results[1].Value.GetProperty("id").GetGuid());Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM support_tickets"));
   Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",(await service.Execute("CreateTicket",owner,family,null,input with{Subject="Different"},"create",null,default)).Error?.Code);
   Assert.Equal("NOT_FOUND",(await service.Execute("GetTicket",stranger,sf,id,new SupportQuery(),null,null,default)).Error?.Code);
   Assert.Equal("NOT_FOUND",(await service.Execute("Message",stranger,sf,id,new MessageRequest("Other"),"message",null,default)).Error?.Code);
   var invalid=await service.Execute("CreateFeedback",owner,family,null,new CreateFeedbackRequest("","",6),"invalid",null,default);Assert.Equal("VALIDATION_ERROR",invalid.Error?.Code);Assert.Contains("rating",invalid.Error!.Errors!.Keys);
   Assert.Equal("SUPPORT_REFERENCE_NOT_FOUND",(await service.Execute("CreateTicket",owner,family,null,input with{SessionId=Guid.NewGuid()},"reference",null,default)).Error?.Code);
   await ExecuteAsync($"ALTER TABLE audit_logs ADD CONSTRAINT support_audit_fault CHECK(target_id<>'{id}'::uuid) NOT VALID");
   await Assert.ThrowsAsync<PostgresException>(()=>service.Execute("Message",owner,family,id,new MessageRequest("Rollback"),"message",null,default));
   Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM support_ticket_messages"));Assert.Equal(1L,await ScalarAsync($"SELECT revision FROM support_tickets WHERE id='{id}'"));Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM support_command_receipts"));
   await ExecuteAsync("ALTER TABLE audit_logs DROP CONSTRAINT support_audit_fault");
   var messages=await Task.WhenAll(service.Execute("Message",owner,family,id,new MessageRequest("Once"),"message",null,default),other.Execute("Message",owner,family,id,new MessageRequest("Once"),"message",null,default));Assert.All(messages,x=>Assert.True(x.IsSuccess,x.Error?.Code));Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM support_ticket_messages"));Assert.Equal(2L,await ScalarAsync($"SELECT revision FROM support_tickets WHERE id='{id}'"));
   await using var con=new NpgsqlConnection(runtime);await con.OpenAsync();await Assert.ThrowsAsync<PostgresException>(()=>new NpgsqlCommand("INSERT INTO support_tickets(id) VALUES(gen_random_uuid())",con).ExecuteNonQueryAsync());
   await Assert.ThrowsAsync<PostgresException>(()=>ExecuteAsync("DELETE FROM support_ticket_messages"));
   await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{family}'");Assert.Equal("UNAUTHORIZED",(await service.Execute("Message",owner,family,id,new MessageRequest("Once"),"message",null,default)).Error?.Code);
  });
 }
 [PostgresFact]
 public async Task Support_status_etag_pagination_closed_replay_and_resource_tenant_audit()
 {
  var f=await SeedReadyPackage();var family=await SeedPlaytestFamily(f.Owner);var af=await SeedPlaytestFamily(adminId);var org=(Guid)(await ScalarAsync($"SELECT organization_id FROM users WHERE id='{f.Owner}'"))!;
  await WithSupportRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);var service=new SupportService(db);
   var feedback=await service.Execute("CreateFeedback",f.Owner,family,null,new CreateFeedbackRequest("Bug","Fixture",5),"feedback",null,default);Assert.True(feedback.IsSuccess,feedback.Error?.Code);var fid=feedback.Value.GetProperty("id").GetGuid();
   var created=await service.Execute("CreateTicket",f.Owner,family,null,new CreateTicketRequest("Fixture","Help",fid),"ticket",null,default);Assert.True(created.IsSuccess,created.Error?.Code);var id=created.Value.GetProperty("id").GetGuid();
   Assert.Equal("PRECONDITION_REQUIRED",(await service.Execute("FeedbackStatus",adminId,af,fid,new AdminFeedbackUpdateRequest(FeedbackStatus.Reviewed),null,null,default)).Error?.Code);
   Assert.True((await service.Execute("FeedbackStatus",adminId,af,fid,new AdminFeedbackUpdateRequest(FeedbackStatus.Reviewed),null,1,default)).IsSuccess);
   Assert.Equal("PRECONDITION_FAILED",(await service.Execute("FeedbackStatus",adminId,af,fid,new AdminFeedbackUpdateRequest(FeedbackStatus.Closed),null,1,default)).Error?.Code);
   for(int n=0;n<23;n++)Assert.True((await service.Execute("Message",f.Owner,family,id,new MessageRequest("Message "+n),"msg-"+n,null,default)).IsSuccess);
   var detail=await service.Execute("GetTicket",f.Owner,family,id,new SupportQuery(2,20),null,null,default);Assert.Equal(23,detail.Value.GetProperty("messages").GetProperty("total").GetInt32());Assert.Equal(3,detail.Value.GetProperty("messages").GetProperty("items").GetArrayLength());
   var revision=detail.Value.GetProperty("revision").GetInt64();Assert.Equal(24,revision);
   Assert.Equal("INVALID_STATUS_TRANSITION",(await service.Execute("TicketStatus",adminId,af,id,new AdminTicketUpdateRequest(SupportTicketStatus.Closed,SupportPriority.High,null),null,revision,default)).Error?.Code);
   foreach(var status in new[]{SupportTicketStatus.InProgress,SupportTicketStatus.Resolved,SupportTicketStatus.Closed})
   {
    var updated=await service.Execute("TicketStatus",adminId,af,id,new AdminTicketUpdateRequest(status,SupportPriority.High,adminId),null,revision,default);Assert.True(updated.IsSuccess,updated.Error?.Code);revision++;
   }
   Assert.Equal("TICKET_CLOSED",(await service.Execute("Message",f.Owner,family,id,new MessageRequest("New"),"closed",null,default)).Error?.Code);
   Assert.True((await service.Execute("Message",f.Owner,family,id,new MessageRequest("Message 0"),"msg-0",null,default)).IsSuccess);
   var reopened=await service.Execute("TicketStatus",adminId,af,id,new AdminTicketUpdateRequest(SupportTicketStatus.Open,SupportPriority.Normal,null),null,revision,default);Assert.True(reopened.IsSuccess,reopened.Error?.Code);Assert.Equal(JsonValueKind.Null,reopened.Value.GetProperty("resolvedAt").ValueKind);
   var page=await service.Execute("AdminListTickets",adminId,af,null,new SupportQuery(Status:"Open",OrganizationId:org),null,null,default);Assert.Equal(1,page.Value.GetProperty("total").GetInt32());Assert.Single(page.Value.GetProperty("items").EnumerateArray());
   Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id IN('{id}','{fid}') AND organization_id IS DISTINCT FROM '{org}'"));
   Assert.Equal("VALIDATION_ERROR",(await service.Execute("ListTickets",f.Owner,family,null,new SupportQuery(PageSize:101),null,null,default)).Error?.Code);
  });
 }
 [PostgresFact]
 public async Task Support_HTTP_headers_ETag_roles_and_validation_have_problem_codes()
 {
  var owner=await SeedSupportUser();var email=(string)(await ScalarAsync($"SELECT email FROM users WHERE id='{owner}'"))!;client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync(email)).AccessToken);
  var missing=await client.PostAsJsonAsync("/api/support/tickets",new{subject="Fixture",description="Help"});Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);Assert.Contains("IDEMPOTENCY_KEY_REQUIRED",await missing.Content.ReadAsStringAsync());
  client.DefaultRequestHeaders.Add("Idempotency-Key","http");var created=await client.PostAsJsonAsync("/api/support/tickets",new{subject="Fixture",description="Help"});Assert.Equal(HttpStatusCode.Created,created.StatusCode);var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
  var detail=await client.GetAsync($"/api/support/tickets/{id}?pageSize=1");Assert.Equal(HttpStatusCode.OK,detail.StatusCode);Assert.Equal("\"support-1\"",detail.Headers.ETag!.ToString());
  Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/admin/support/tickets")).StatusCode);
  client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
  var body=new{status="InProgress",priority="Normal",assignedTo=(Guid?)null};
  Assert.Equal((HttpStatusCode)428,(await client.PatchAsJsonAsync($"/api/admin/support/tickets/{id}",body)).StatusCode);
  client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match","bad");Assert.Equal(HttpStatusCode.BadRequest,(await client.PatchAsJsonAsync($"/api/admin/support/tickets/{id}",body)).StatusCode);
  client.DefaultRequestHeaders.Remove("If-Match");client.DefaultRequestHeaders.Add("If-Match","\"support-1\"");var updated=await client.PatchAsJsonAsync($"/api/admin/support/tickets/{id}",body);Assert.Equal(HttpStatusCode.OK,updated.StatusCode);Assert.Equal("\"support-2\"",updated.Headers.ETag!.ToString());
  Assert.Equal(HttpStatusCode.PreconditionFailed,(await client.PatchAsJsonAsync($"/api/admin/support/tickets/{id}",body)).StatusCode);
 }

 [PostgresFact]
 public async Task Support_close_and_message_race_preserves_history_and_ETag()
 {
  var owner=await SeedSupportUser();var family=await SeedPlaytestFamily(owner);var af=await SeedPlaytestFamily(adminId);
  await WithSupportRuntime(async runtime=>
  {
   await using var db=BuildingContext(runtime);await using var otherDb=BuildingContext(runtime);var service=new SupportService(db);var other=new SupportService(otherDb);
   var ticket=await service.Execute("CreateTicket",owner,family,null,new CreateTicketRequest("Race","Help"),"create",null,default);var id=ticket.Value.GetProperty("id").GetGuid();
   Assert.True((await service.Execute("TicketStatus",adminId,af,id,new AdminTicketUpdateRequest(SupportTicketStatus.InProgress,SupportPriority.Normal,null),null,1,default)).IsSuccess);
   Assert.True((await service.Execute("TicketStatus",adminId,af,id,new AdminTicketUpdateRequest(SupportTicketStatus.Resolved,SupportPriority.Normal,null),null,2,default)).IsSuccess);
   var close=service.Execute("TicketStatus",adminId,af,id,new AdminTicketUpdateRequest(SupportTicketStatus.Closed,SupportPriority.Normal,null),null,3,default);
   var message=other.Execute("Message",owner,family,id,new MessageRequest("Concurrent"),"message",null,default);
   await Task.WhenAll(close,message);
   if(close.Result.IsSuccess){Assert.Equal("TICKET_CLOSED",message.Result.Error?.Code);Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM support_ticket_messages"));}
   else{Assert.Equal("PRECONDITION_FAILED",close.Result.Error?.Code);Assert.True(message.Result.IsSuccess);Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM support_ticket_messages"));}
   Assert.Equal(4L,await ScalarAsync($"SELECT revision FROM support_tickets WHERE id='{id}'"));
  });
 }
}
