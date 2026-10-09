using Fire3D.API.Authorization;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Authorize(Roles="OrganizationUser,PlatformAdmin")]
public sealed class ScenarioReviewQueriesController(IScenarioReviewQueries queries) : ControllerBase
{
    /// <summary>PlatformAdmin review queue, ordered by submittedAt and reviewId descending.</summary>
    /// <remarks>Status: Submitted, Approved or Rejected. Page size defaults to 20, maximum 100. Reads recheck active account, organization and session family.</remarks>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("/api/admin/scenario-reviews")]
    [ProducesResponseType<PageResponse<ScenarioReviewSummary>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<IActionResult> List([FromQuery]string? status=null,[FromQuery]Guid? organizationId=null,[FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)
        =>Respond(await queries.ListAsync(User.GetActorId(),User.GetSessionFamilyId(),status,organizationId,page,pageSize,ct));

    /// <summary>PlatformAdmin reads frozen scenario content, rubric, decision and current technical readiness.</summary>
    [Authorize(Roles="PlatformAdmin")]
    [HttpGet("/api/admin/scenario-reviews/{reviewId:guid}")]
    [ProducesResponseType<ScenarioReviewDetail>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Detail(Guid reviewId,CancellationToken ct)
        =>Respond(await queries.DetailAsync(User.GetActorId(),User.GetSessionFamilyId(),reviewId,false,ct));

    /// <summary>OrganizationUser reads their tenant's review decision after reload; PlatformAdmin may read any tenant.</summary>
    [HttpGet("/api/scenario-versions/{versionId:guid}/review")]
    [ProducesResponseType<ScenarioReviewDetail>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Version(Guid versionId,CancellationToken ct)
        =>Respond(await queries.DetailAsync(User.GetActorId(),User.GetSessionFamilyId(),versionId,true,ct));

    private ObjectResult Respond<T>(AuthResult<T> result)=>result.IsSuccess?Ok(result.Value):Problem(statusCode:result.Error!.Status,title:result.Error.Message,
        extensions:new Dictionary<string,object?>{["code"]=result.Error.Code,["errors"]=result.Error.Errors});
}
