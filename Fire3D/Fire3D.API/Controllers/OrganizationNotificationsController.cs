using Fire3D.API.Authorization;
using Fire3D.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;

[ApiController][Authorize(Roles="OrganizationUser")][ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
[ProducesResponseType(typeof(ProblemDetails),400)][ProducesResponseType(typeof(ProblemDetails),401)]
[ProducesResponseType(typeof(ProblemDetails),403)][ProducesResponseType(typeof(ProblemDetails),404)]
public sealed class OrganizationNotificationsController(IOrganizationNotifications notifications) : ControllerBase
{
    /// <summary>Reminders addressed to the current OrganizationUser in their organization, newest first. Filters: unread, type (BuildingServiceExpiring); page 20 by default, maximum 100.</summary>
    [HttpGet("api/organizations/me/notifications")]
    [ProducesResponseType(typeof(BillingPage<OrganizationNotificationResponse>),200)]
    public async Task<BillingPage<OrganizationNotificationResponse>> List([FromQuery]NotificationQuery query,CancellationToken ct)
        =>await notifications.List(User.GetActorId(),User.GetSessionFamilyId(),query,ct);
    /// <summary>Marks one of the caller's notifications as read. Idempotent: the first read time is kept.</summary>
    [HttpPatch("api/organizations/me/notifications/{id:guid}/read")]
    [ProducesResponseType(typeof(OrganizationNotificationResponse),200)]
    public async Task<OrganizationNotificationResponse> Read(Guid id,CancellationToken ct)
        =>await notifications.MarkRead(User.GetActorId(),User.GetSessionFamilyId(),id,ct);
}
