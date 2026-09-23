using Fire3D.API.Authorization;
using Fire3D.Application.Releases;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/releases")]
[Authorize]
public class ReleasesController(ISender sender) : ControllerBase
{
    /// <summary>Creates a Built release and pins its immutable package metadata.</summary>
    /// <remarks>The revision and scenario version must have a matching ConfirmForTraining review. This operation records the completed build; it does not run Unity inside the HTTP request.</remarks>
    [HttpPost]
    [ProducesResponseType<ReleaseResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<ActionResult<ReleaseResponse>> BuildRelease(BuildReleaseRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new BuildReleaseCommand(User.GetActorId(), request), ct);
        return result.IsSuccess
            ? Created($"/api/releases/{result.Value!.Id}", result.Value)
            : ReleaseProblem(result.Error!);
    }

    /// <summary>Gets a release and its pinned package metadata.</summary>
    [HttpGet("{releaseId:guid}")]
    [ProducesResponseType<ReleaseResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<ReleaseResponse>> GetRelease(Guid releaseId, CancellationToken ct)
    {
        var result = await sender.Send(new GetReleaseQuery(User.GetActorId(), releaseId), ct);
        return result.IsSuccess ? Ok(result.Value) : ReleaseProblem(result.Error!);
    }

    /// <summary>
    /// Publishes a release (D17).
    /// </summary>
    [HttpPost("{releaseId:guid}/publish")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> PublishRelease(Guid releaseId, CancellationToken ct)
    {
        var actor = User.GetActorId();

        var result = await sender.Send(new Fire3D.Application.Releases.Commands.PublishRelease.PublishReleaseCommand(actor, releaseId), ct);

        return result.IsSuccess ? NoContent() : ReleaseProblem(result.Error!);
    }

    /// <summary>Revokes a Built or Published release. Repeating the request is idempotent.</summary>
    [HttpPost("{releaseId:guid}/revoke")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> RevokeRelease(Guid releaseId, RevokeReleaseRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new RevokeReleaseCommand(User.GetActorId(), releaseId, request), ct);
        return result.IsSuccess ? NoContent() : ReleaseProblem(result.Error!);
    }

    private ObjectResult ReleaseProblem(Fire3D.Application.Authentication.AuthError error) =>
        Problem(statusCode: error.Status, title: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
}
