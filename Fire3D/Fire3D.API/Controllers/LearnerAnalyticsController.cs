using System.Text.Json;
using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class LearnerAnalyticsController(ILearnerAnalytics analytics) : ControllerBase
{
    /// <summary>OrganizationUser training analytics for the caller's organization plus its AI usage. UTC [from,to), default 30 days, maximum 90.</summary>
    [Authorize(Roles="OrganizationUser")]
    [HttpGet("api/organizations/me/analytics/training")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public Task<IActionResult> Organization([FromQuery]string? from,[FromQuery]string? to,CancellationToken ct)=>Read("OrganizationTraining",from,to,ct);

    /// <summary>PlatformAdmin training analytics across organizations, with per-organization breakdown and platform AI usage.</summary>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("api/admin/analytics/training")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public Task<IActionResult> PlatformTraining([FromQuery]string? from,[FromQuery]string? to,CancellationToken ct)=>Read("PlatformTraining",from,to,ct);

    /// <summary>PlatformAdmin revenue: Applied payment transactions by receipt time, per currency, purpose and UTC day.</summary>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("api/admin/analytics/revenue")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public Task<IActionResult> Revenue([FromQuery]string? from,[FromQuery]string? to,CancellationToken ct)=>Read("PlatformRevenue",from,to,ct);

    private async Task<IActionResult> Read(string report,string? from,string? to,CancellationToken ct)
    {
        var result=await analytics.Read(report,User.GetActorId(),User.GetSessionFamilyId(),from,to,ct);
        return result.IsSuccess?Ok(result.Value):Problem(statusCode:result.Error!.Status,title:result.Error.Message,extensions:new Dictionary<string,object?>{["code"]=result.Error.Code});
    }
}
