using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Enums;
namespace Fire3D.Application.Support;
public sealed record CreateFeedbackRequest(string Category,string Message,int? Rating=null,Guid? SessionId=null);
public sealed record CreateTicketRequest(string Subject,string Description,Guid? FeedbackId=null,Guid? SessionId=null);
public sealed record MessageRequest(string Message);
public sealed record AdminTicketUpdateRequest(SupportTicketStatus Status,SupportPriority Priority,Guid? AssignedTo);
public sealed record AdminFeedbackUpdateRequest(FeedbackStatus Status);
public sealed record SupportQuery(int Page=1,int PageSize=20,string? Status=null,string? Priority=null,Guid? AssignedTo=null,Guid? OrganizationId=null);
public sealed record SupportPage<T>(IReadOnlyList<T> Items,long Total,int Page,int PageSize);
public sealed record FeedbackResponse(Guid Id,string Category,string Message,int? Rating,string Status,Guid? ReviewedBy,DateTime? ReviewedAt,long Revision,DateTime CreatedAt,DateTime UpdatedAt);
public sealed record SupportMessageResponse(Guid Id,Guid AuthorId,string Message,DateTime CreatedAt);
public sealed record TicketResponse(Guid Id,string TicketNumber,string Subject,string Description,string Status,string Priority,Guid? AssignedTo,DateTime? ResolvedAt,long Revision,DateTime CreatedAt,DateTime UpdatedAt,SupportPage<SupportMessageResponse>? Messages=null);
public interface ISupportService
{
 Task<AuthResult<JsonElement>> Execute(string action,Guid actor,Guid family,Guid? resource,object input,string? key,long? expected,CancellationToken ct);
}
