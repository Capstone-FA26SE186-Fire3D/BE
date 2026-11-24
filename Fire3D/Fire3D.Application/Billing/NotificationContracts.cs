namespace Fire3D.Application.Billing;

/// <summary>
/// One notification for the current recipient. Type BuildingServiceExpiring: EntitlementId and ReferenceEndsAt are set and
/// StillRelevant is false once the Building has a later paid entitlement (renewal) or the referenced period has ended.
/// Types ContentReviewApproved / ContentReviewRejected (BE#211): the thẩm định result of a scenario version the recipient submitted;
/// ContentReviewId and ScenarioVersionId are set, the body carries the reason of a rejection, StillRelevant is always true.
/// Types FireInfrastructureApproved / FireInfrastructureRejected (BE#213): the thẩm định result of a fire infrastructure version the
/// recipient submitted; FireInfrastructureId is set, the body carries the reason of a rejection, StillRelevant is always true.
/// The history is kept either way.
/// </summary>
public sealed class OrganizationNotificationResponse
{
    public Guid Id { get; init; }
    public string Type { get; init; }="";
    public string Title { get; init; }="";
    public string Body { get; init; }="";
    public Guid BuildingId { get; init; }
    public string? BuildingName { get; init; }
    public Guid? EntitlementId { get; init; }
    public DateTime? ReferenceEndsAt { get; init; }
    public Guid? ContentReviewId { get; init; }
    public Guid? ScenarioVersionId { get; init; }
    public Guid? FireInfrastructureId { get; init; }
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
