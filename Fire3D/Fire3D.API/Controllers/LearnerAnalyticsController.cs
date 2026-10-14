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
    /// <summary>OrganizationUser training analytics for the caller's organization plus its AI usage.</summary>
    /// <remarks>UTC [from,to), default 30 days, maximum 90. Filters buildingId and scenarioId stay inside the report scope (another tenant's id is 404);
    /// groupBy day|week|month (weeks start Monday)|building|scenario adds cohorts, with uniqueTrainees counted per group. Completed excludes
    /// Abandoned; duration is runtime play time. Playtests and preparations are never counted. See definitions in the response.</remarks>
    [Authorize(Roles="OrganizationUser")]
    [HttpGet("api/organizations/me/analytics/training")]
    [ProducesResponseType<TrainingAnalyticsReport>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public Task<IActionResult> Organization([FromQuery]string? from,[FromQuery]string? to,[FromQuery]Guid? buildingId,[FromQuery]Guid? scenarioId,[FromQuery]string? groupBy,CancellationToken ct)=>
        Report(new("Organization",null,buildingId,scenarioId,groupBy,from,to),ct);

    /// <summary>PlatformAdmin training analytics across organizations, with per-organization breakdown and platform AI usage.</summary>
    /// <remarks>UTC [from,to), default 30 days, maximum 90. Same filters and definitions as the organization report.</remarks>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("api/admin/analytics/training")]
    [ProducesResponseType<TrainingAnalyticsReport>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public Task<IActionResult> PlatformTraining([FromQuery]string? from,[FromQuery]string? to,[FromQuery]Guid? buildingId,[FromQuery]Guid? scenarioId,[FromQuery]string? groupBy,CancellationToken ct)=>
        Report(new("Platform",null,buildingId,scenarioId,groupBy,from,to),ct);

    /// <summary>PlatformAdmin training analytics and AI usage of one organization, with the same definitions as the organization's own report.</summary>
    /// <remarks>UTC [from,to), default 30 days, maximum 90. Inactive organizations are included; deleted or unknown ones return 404. Same filters as the organization report.</remarks>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("api/admin/organizations/{organizationId:guid}/analytics/training")]
    [ProducesResponseType<TrainingAnalyticsReport>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public Task<IActionResult> OrganizationTraining(Guid organizationId,[FromQuery]string? from,[FromQuery]string? to,[FromQuery]Guid? buildingId,[FromQuery]Guid? scenarioId,[FromQuery]string? groupBy,CancellationToken ct)=>
        Report(new("PlatformOrganization",organizationId,buildingId,scenarioId,groupBy,from,to),ct);

    /// <summary>CSV export of the organization's training cohorts with the same filters (groupBy defaults to day).</summary>
    /// <remarks>UTF-8 with BOM, fixed header, aggregate rows only (no Trainee name, email or ID), formula-leading cells neutralised.
    /// 422 EXPORT_ROW_LIMIT_EXCEEDED above 10000 rows (never truncated); 429 EXPORT_DAILY_LIMIT_EXCEEDED after 10 exports per account per
    /// UTC day, with Retry-After. Every export is audited.</remarks>
    [Authorize(Roles="OrganizationUser")]
    [HttpGet("api/organizations/me/analytics/training/export")]
    [ProducesResponseType<string>(200,"text/csv")]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(429)]
    public Task<IActionResult> ExportOrganization([FromQuery]string? from,[FromQuery]string? to,[FromQuery]Guid? buildingId,[FromQuery]Guid? scenarioId,[FromQuery]string? groupBy,CancellationToken ct)=>
        Export(new("Organization",null,buildingId,scenarioId,groupBy,from,to),ct);

    /// <summary>CSV export of platform-wide training cohorts (same rules as the organization export).</summary>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("api/admin/analytics/training/export")]
    [ProducesResponseType<string>(200,"text/csv")]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(429)]
    public Task<IActionResult> ExportPlatform([FromQuery]string? from,[FromQuery]string? to,[FromQuery]Guid? buildingId,[FromQuery]Guid? scenarioId,[FromQuery]string? groupBy,CancellationToken ct)=>
        Export(new("Platform",null,buildingId,scenarioId,groupBy,from,to),ct);

    /// <summary>CSV export of one organization's training cohorts for PlatformAdmin (same rules as the organization export).</summary>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("api/admin/organizations/{organizationId:guid}/analytics/training/export")]
    [ProducesResponseType<string>(200,"text/csv")]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(429)]
    public Task<IActionResult> ExportOrganizationAdmin(Guid organizationId,[FromQuery]string? from,[FromQuery]string? to,[FromQuery]Guid? buildingId,[FromQuery]Guid? scenarioId,[FromQuery]string? groupBy,CancellationToken ct)=>
        Export(new("PlatformOrganization",organizationId,buildingId,scenarioId,groupBy,from,to),ct);

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
        return result.IsSuccess?Ok(result.Value):Failure(result.Error!);
    }
    private async Task<IActionResult> Report(TrainingAnalyticsQuery query,CancellationToken ct)
    {
        var result=await analytics.ReadTraining(User.GetActorId(),User.GetSessionFamilyId(),query,ct);
        return result.IsSuccess?Ok(result.Value):Failure(result.Error!);
    }
    private async Task<IActionResult> Export(TrainingAnalyticsQuery query,CancellationToken ct)
    {
        var result=await analytics.ExportTraining(User.GetActorId(),User.GetSessionFamilyId(),query,ct);
        return result.IsSuccess?File(result.Value!.Content,"text/csv; charset=utf-8",result.Value.FileName):Failure(result.Error!);
    }
    private ObjectResult Failure(AuthError error)
    {
        if(error.RetryAfterSeconds is int retry)Response.Headers.RetryAfter=retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(statusCode:error.Status,title:error.Message,extensions:new Dictionary<string,object?>{["code"]=error.Code});
    }
}
