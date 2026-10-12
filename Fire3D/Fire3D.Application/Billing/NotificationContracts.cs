namespace Fire3D.Application.Billing;

/// <summary>
/// One reminder for the current recipient. StillRelevant is false once the Building has a later paid entitlement (renewal) or the
/// referenced period has ended; the history is kept either way.
/// </summary>
public sealed class OrganizationNotificationResponse
{
    public Guid Id { get; init; }
    public string Type { get; init; }="";
    public string Title { get; init; }="";
    public string Body { get; init; }="";
    public Guid BuildingId { get; init; }
    public string? BuildingName { get; init; }
    public Guid EntitlementId { get; init; }
    public DateTime ReferenceEndsAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ReadAt { get; init; }
    public bool StillRelevant { get; init; }
}
public sealed record NotificationQuery(bool? Unread=null,string? Type=null,int Page=1,int PageSize=20);
public interface IOrganizationNotifications
{
    Task<BillingPage<OrganizationNotificationResponse>> List(Guid actor,Guid family,NotificationQuery query,CancellationToken ct);
    Task<OrganizationNotificationResponse> MarkRead(Guid actor,Guid family,Guid id,CancellationToken ct);
}
