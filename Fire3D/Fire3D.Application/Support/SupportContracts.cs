using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Enums;
namespace Fire3D.Application.Support;
public sealed record CreateFeedbackRequest(string Category,string Message,int? Rating=null,Guid? SessionId=null);
public sealed record CreateTicketRequest(string Subject,string Description,Guid? FeedbackId=null,Guid? SessionId=null);
public sealed record MessageRequest(string Message);
/// <summary>Reason is optional (1-1000 characters, not only whitespace) and is recorded only in the audit trail.</summary>
public sealed record AdminTicketUpdateRequest(SupportTicketStatus Status,SupportPriority Priority,Guid? AssignedTo,string? Reason=null);
public sealed record AdminFeedbackUpdateRequest(FeedbackStatus Status,string? Reason=null);
public sealed record SupportQuery(int Page=1,int PageSize=20,string? Status=null,string? Priority=null,Guid? AssignedTo=null,Guid? OrganizationId=null);
public sealed record SupportPage<T>(IReadOnlyList<T> Items,long Total,int Page,int PageSize);
/// <summary>Ticket count per status in the caller's scope with the priority/assignedTo/organizationId filters, ignoring the status filter. Every key is always present.</summary>
public sealed record SupportTicketStatusCounts([property:JsonPropertyName("Open")]long Open,[property:JsonPropertyName("InProgress")]long InProgress,[property:JsonPropertyName("Resolved")]long Resolved,[property:JsonPropertyName("Closed")]long Closed);
/// <summary>Ticket list page; total is for the current filters including status, counts ignore status.</summary>
public sealed record SupportTicketPage(IReadOnlyList<TicketResponse> Items,long Total,int Page,int PageSize,SupportTicketStatusCounts Counts);
/// <summary>Current display label of a person. Email is present only in PlatformAdmin responses; Available=false when the account is missing or deleted (the ID is kept).</summary>
public sealed record SupportPerson(Guid Id,string? DisplayName,bool Available,string? Email=null);
/// <summary>Organization stored on the support resource (not the creator's current membership); Name is null when it no longer exists.</summary>
public sealed record SupportOrganization(Guid Id,string? Name,bool Available);
public sealed record FeedbackResponse(Guid Id,string Category,string Message,int? Rating,string Status,Guid? ReviewedBy,DateTime? ReviewedAt,long Revision,DateTime CreatedAt,DateTime UpdatedAt,SupportPerson? CreatedBy=null,SupportOrganization? Organization=null);
public sealed record SupportMessageResponse(Guid Id,Guid AuthorId,string Message,DateTime CreatedAt,SupportPerson? Author=null);
public sealed record TicketResponse(Guid Id,string TicketNumber,string Subject,string Description,string Status,string Priority,Guid? AssignedTo,DateTime? ResolvedAt,long Revision,DateTime CreatedAt,DateTime UpdatedAt,SupportPage<SupportMessageResponse>? Messages=null,SupportPerson? CreatedBy=null,SupportOrganization? Organization=null);
public interface ISupportService
{
 Task<AuthResult<JsonElement>> Execute(string action,Guid actor,Guid family,Guid? resource,object input,string? key,long? expected,CancellationToken ct);
}
