using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Support;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
namespace Fire3D.Infrastructure.Support;
public sealed class SupportService(Fire3DDbContext db):ISupportService
{
 public Task<AuthResult<JsonElement>> Execute(string action,Guid actor,Guid family,Guid? resource,object input,string? key,long? expected,CancellationToken ct)
 {
  var errors=new Dictionary<string,string[]>();
  void Text(string field,string? text,int max){if(string.IsNullOrWhiteSpace(text)||text.Length>max)errors[field]=[$"Provide 1-{max} characters."];}
  switch(input)
  {
   case CreateFeedbackRequest r:
    Text("category",r.Category,100);Text("message",r.Message,10000);if(r.Rating is <1 or >5)errors["rating"]=["Rating must be 1-5."];
    input=r with{Category=r.Category?.Trim()!,Message=r.Message?.Trim()!};break;
   case CreateTicketRequest r:Text("subject",r.Subject,255);Text("description",r.Description,10000);input=r with{Subject=r.Subject?.Trim()!,Description=r.Description?.Trim()!};break;
   case MessageRequest r:Text("message",r.Message,10000);input=r with{Message=r.Message?.Trim()!};break;
   case AdminFeedbackUpdateRequest r:if(!Enum.IsDefined(r.Status))errors["status"]=["Invalid feedback status."];input=new{status=r.Status.ToString()};break;
   case AdminTicketUpdateRequest r:
    if(!Enum.IsDefined(r.Status))errors["status"]=["Invalid ticket status."];if(!Enum.IsDefined(r.Priority))errors["priority"]=["Invalid ticket priority."];input=new{status=r.Status.ToString(),priority=r.Priority.ToString(),r.AssignedTo};break;
   case SupportQuery q:
    if(q.Page<1 || q.Page>100000)errors["page"]=["Page must be 1-100000."];
    if(q.PageSize<1 || q.PageSize>100)errors["pageSize"]=["Page size must be 1-100."];
    if(q.Status is not null && !(action.Contains("Feedback")?Enum.GetNames<FeedbackStatus>():Enum.GetNames<SupportTicketStatus>()).Contains(q.Status))errors["status"]=["Invalid status."];
    if(q.Priority is not null && !Enum.GetNames<SupportPriority>().Contains(q.Priority))errors["priority"]=["Invalid priority."];
    break;
  }
  if(errors.Count>0)return Task.FromResult(AuthResult<JsonElement>.Fail("VALIDATION_ERROR","Check the indicated request fields.",400,errors));
  return JsonCommandGate.Execute(db,"support_command_gate",action,actor,family,resource,input,key,expected,ct);
 }
}
