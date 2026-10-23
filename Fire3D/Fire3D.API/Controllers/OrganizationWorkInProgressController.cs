using Fire3D.API.Authorization;
using Fire3D.Application.Scenarios.Queries.GetOrganizationWorkInProgress;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Authorize(Roles = "OrganizationUser")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrganizationWorkInProgressController(ISender sender) : ControllerBase
{
    /// <summary>The OrganizationUser's organization's most recently saved scenario drafts, for the "continue authoring" shortcut on the Building list.</summary>
    /// <remarks>Items are scenario drafts (kind ScenarioDraft) on active, not deleted Buildings of the caller's organization, newest save first
    /// (updatedAt), including drafts whose scenario already has snapshots. limit 1–5, default 1; other values 400 VALIDATION_ERROR.
    /// draftNumber is the draft's own number within its scenario. latestVersionNumber/latestVersionReviewStatus (NotSubmitted, Submitted, Approved, Rejected) describe the scenario's newest snapshot and
    /// are null without one. Drafts are mutable and versions immutable, so resuming a draft never edits an approved version.
    /// An organization without drafts returns items []. PlatformAdmin and Trainee are 403.</remarks>
    [HttpGet("api/organizations/me/work-in-progress")]
    [ProducesResponseType<OrganizationWorkInProgressResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<IActionResult> Get([FromQuery] int limit = 1, CancellationToken ct = default)
    {
        var result = await sender.Send(new GetOrganizationWorkInProgressQuery(User.GetActorId(), User.GetSessionFamilyId(), limit), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }
}
