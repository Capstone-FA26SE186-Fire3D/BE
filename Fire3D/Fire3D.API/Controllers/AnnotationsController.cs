using Fire3D.API.Authorization;
using Fire3D.Application.Ifc;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController, Authorize]
[Route("api/revisions/{revisionId:guid}/annotations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AnnotationsController(ISender sender) : ControllerBase
{
    /// <summary>Reads the latest annotation overlay and its ETag.</summary>
    /// <remarks>OrganizationUser in the revision tenant or PlatformAdmin. Empty overlay has version 0 and ETag "0".</remarks>
    [HttpGet]
    [ProducesResponseType<AnnotationSnapshot>(200)]
    public async Task<IActionResult> Get(Guid revisionId, CancellationToken ct) =>
        Respond(await sender.Send(new GetAnnotationsQuery(User.GetActorId(), revisionId), ct));

    /// <summary>Replaces the annotation overlay by appending a version.</summary>
    /// <remarks>Send If-Match from GET. Missing header:428; stale version:412; invalid or foreign IFC anchor:400.
    /// Body: items array with id, ifcGlobalId, label and optional note. Maximum 500 items.
    /// Does not modify geometry, exits or runtime artifacts. Returns 200 and a new ETag.</remarks>
    [HttpPut]
    [RequestSizeLimit(2_000_000)]
    [ProducesResponseType<AnnotationSnapshot>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    public async Task<IActionResult> Put(Guid revisionId, [FromBody] AnnotationData data,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
        Respond(await sender.Send(new SaveAnnotationsCommand(User.GetActorId(), revisionId, ifMatch, data), ct));

    private IActionResult Respond(Fire3D.Application.Authentication.AuthResult<AnnotationSnapshot> result)
    {
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
        Response.Headers.ETag = result.Value!.ETag;
        return Ok(result.Value);
    }
}
