using Fire3D.API.Authorization;
using Fire3D.Application.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Globalization;
namespace Fire3D.API.Controllers;
[ApiController][Authorize][ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
[ProducesResponseType(typeof(ProblemDetails),400)][ProducesResponseType(typeof(ProblemDetails),401)]
[ProducesResponseType(typeof(ProblemDetails),403)][ProducesResponseType(typeof(ProblemDetails),404)]
[ProducesResponseType(typeof(ProblemDetails),409)][ProducesResponseType(typeof(ProblemDetails),412)]
[ProducesResponseType(typeof(ProblemDetails),428)]
public sealed class SupportController(ISupportService service):ControllerBase
{

 [Authorize(Roles="Trainee,OrganizationUser")][HttpPost("api/feedback")][ProducesResponseType(typeof(FeedbackResponse),201)]
 public Task<IActionResult> CreateFeedback(CreateFeedbackRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)=>Run("CreateFeedback",null,request,key,null,201,ct);

 [Authorize(Roles="Trainee,OrganizationUser")][HttpGet("api/feedback")][ProducesResponseType(typeof(SupportPage<FeedbackResponse>),200)]
 public Task<IActionResult> ListFeedback([FromQuery]SupportQuery query,CancellationToken ct)=>Run("ListFeedback",null,query,null,null,200,ct);

 [Authorize(Roles="Trainee,OrganizationUser")][HttpPost("api/support/tickets")][ProducesResponseType(typeof(TicketResponse),201)]
 public Task<IActionResult> CreateTicket(CreateTicketRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)=>Run("CreateTicket",null,request,key,null,201,ct);

 [Authorize(Roles="Trainee,OrganizationUser")][HttpGet("api/support/tickets")][ProducesResponseType(typeof(SupportPage<TicketResponse>),200)]
 public Task<IActionResult> ListTickets([FromQuery]SupportQuery query,CancellationToken ct)=>Run("ListTickets",null,query,null,null,200,ct);

 [Authorize(Roles="Trainee,OrganizationUser")][HttpGet("api/support/tickets/{id:guid}")][ProducesResponseType(typeof(TicketResponse),200)]
 public Task<IActionResult> GetTicket(Guid id,[FromQuery]SupportQuery query,CancellationToken ct)=>Run("GetTicket",id,query,null,null,200,ct);

 [Authorize(Roles="Trainee,OrganizationUser")][HttpPost("api/support/tickets/{id:guid}/messages")][ProducesResponseType(typeof(SupportMessageResponse),201)]
 public Task<IActionResult> AddMessage(Guid id,MessageRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)=>Run("Message",id,request,key,null,201,ct);

 [Authorize(Roles="PlatformAdmin")][HttpGet("api/admin/feedback")][ProducesResponseType(typeof(SupportPage<FeedbackResponse>),200)]
 public Task<IActionResult> AdminFeedback([FromQuery]SupportQuery query,CancellationToken ct)=>Run("AdminListFeedback",null,query,null,null,200,ct);

 [Authorize(Roles="PlatformAdmin")][HttpPatch("api/admin/feedback/{id:guid}/status")][ProducesResponseType(typeof(FeedbackResponse),200)]
 [Fire3D.API.OpenApi.ResponseHeader("ETag")]
 public Task<IActionResult> AdminUpdateFeedback(Guid id,AdminFeedbackUpdateRequest request,[FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader]string? match,CancellationToken ct)=>Patch("FeedbackStatus",id,request,match,ct);

 [Authorize(Roles="PlatformAdmin")][HttpGet("api/admin/support/tickets")][ProducesResponseType(typeof(SupportPage<TicketResponse>),200)]
 public Task<IActionResult> AdminTickets([FromQuery]SupportQuery query,CancellationToken ct)=>Run("AdminListTickets",null,query,null,null,200,ct);

 [Authorize(Roles="PlatformAdmin")][HttpGet("api/admin/support/tickets/{id:guid}")][ProducesResponseType(typeof(TicketResponse),200)]
 [Fire3D.API.OpenApi.ResponseHeader("ETag")]
 public Task<IActionResult> AdminTicket(Guid id,[FromQuery]SupportQuery query,CancellationToken ct)=>Run("AdminGetTicket",id,query,null,null,200,ct);

 [Authorize(Roles="PlatformAdmin")][HttpPost("api/admin/support/tickets/{id:guid}/messages")][ProducesResponseType(typeof(SupportMessageResponse),201)]
 public Task<IActionResult> AdminMessage(Guid id,MessageRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)=>Run("AdminMessage",id,request,key,null,201,ct);

 [Authorize(Roles="PlatformAdmin")][HttpPatch("api/admin/support/tickets/{id:guid}")][ProducesResponseType(typeof(TicketResponse),200)]
 [Fire3D.API.OpenApi.ResponseHeader("ETag")]
 public Task<IActionResult> AdminUpdate(Guid id,AdminTicketUpdateRequest request,[FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader]string? match,CancellationToken ct)=>Patch("TicketStatus",id,request,match,ct);

 private Task<IActionResult> Patch(string action,Guid? id,object input,string? match,CancellationToken ct)
 {
  if(string.IsNullOrEmpty(match))return Task.FromResult<IActionResult>(ProblemCode(428,"PRECONDITION_REQUIRED"));
  if(match.Length<11 || !match.StartsWith("\"support-") || match[^1]!='"' || !long.TryParse(match.AsSpan(9,match.Length-10),NumberStyles.None,CultureInfo.InvariantCulture,out var rev)||rev<1)
   return Task.FromResult<IActionResult>(ProblemCode(400,"INVALID_IF_MATCH"));
  return Run(action,id,input,null,rev,200,ct);
 }
 private async Task<IActionResult> Run(string action,Guid? id,object input,string? key,long? expected,int success,CancellationToken ct)
 {
  var result=await service.Execute(action,User.GetActorId(),User.GetSessionFamilyId(),id,input,key,expected,ct);
  if(!result.IsSuccess)
  {
   var error=result.Error!;return ProblemCode(error.Status,error.Code,error.Message,error.Errors);
  }
  if(result.Value.ValueKind==JsonValueKind.Object && result.Value.TryGetProperty("revision",out var revision))Response.Headers.ETag=$"\"support-{revision.GetInt64()}\"";
  if(success==201 && result.Value.TryGetProperty("id",out var target))Response.Headers.Location=action.Contains("Feedback")?"/api/feedback":$"/api/support/tickets/{id??target.GetGuid()}";
  return StatusCode(success,result.Value);
 }
 private ObjectResult ProblemCode(int status,string code,string? title=null,IReadOnlyDictionary<string,string[]>? errors=null)
 {
  var problem=new ProblemDetails{Status=status,Title=title??"The support operation was rejected."};
  problem.Extensions["code"]=code;problem.Extensions["traceId"]=System.Diagnostics.Activity.Current?.Id??HttpContext.TraceIdentifier;
  if(errors is not null)problem.Extensions["errors"]=errors;return new ObjectResult(problem){StatusCode=status};
 }
}
