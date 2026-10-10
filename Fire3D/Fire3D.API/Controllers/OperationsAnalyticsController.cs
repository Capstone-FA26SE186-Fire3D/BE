using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class OperationsAnalyticsController(IOperationsQueries queries) : ControllerBase
{
    /// <summary>PlatformAdmin operations snapshot: accounts/organizations/buildings, historical created counts, processing kinds/status and tickets. Not training or revenue analytics.</summary>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("api/admin/analytics/operations")]
    [ProducesResponseType<PlatformOperations>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<IActionResult> Platform([FromQuery]string? from,[FromQuery]string? to,CancellationToken ct)
    {
        var result=await queries.Platform(User.GetActorId(),User.GetSessionFamilyId(),from,to,ct);
        return result.IsSuccess?Ok(result.Value):Failure(result.Error!);
    }
    /// <summary>OrganizationUser operations snapshot: own tenant Building/processing and caller-created support tickets. No account aggregates. UTC [from,to), default 30 days, maximum 90.</summary>
    [Authorize(Roles="OrganizationUser")]
    [HttpGet("api/organizations/me/analytics/operations")]
    [ProducesResponseType<OrganizationOperations>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<IActionResult> Organization([FromQuery]string? from,[FromQuery]string? to,CancellationToken ct)
    {
        var result=await queries.Organization(User.GetActorId(),User.GetSessionFamilyId(),from,to,ct);
        return result.IsSuccess?Ok(result.Value):Failure(result.Error!);
    }
    private ObjectResult Failure(AuthError error)=>Problem(statusCode:error.Status,title:error.Message,extensions:new Dictionary<string,object?>{["code"]=error.Code,["errors"]=error.Errors});
}