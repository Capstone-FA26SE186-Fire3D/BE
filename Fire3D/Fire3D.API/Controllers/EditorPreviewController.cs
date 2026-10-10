using Fire3D.API.Authorization;
using Fire3D.Application.Ifc;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController, Authorize]
[Route("api/buildings/{buildingId:guid}/editor-preview")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EditorPreviewController(ISender sender) : ControllerBase
{
    /// <summary>Reads a preview for an explicitly selected IFC revision.</summary>
    /// <remarks>Requires revisionId query and OrganizationUser in the tenant or PlatformAdmin.
    /// Returns 200 Ready with a five-minute signed GET URL, or 200 NotReady without URL.
    /// Only successful current Geometry attempts qualify. Transform, floors and semantic mapping come
    /// from that artifact's metadata. Missing/inaccessible revision or mismatched building returns 404.
    /// S3 HEAD must confirm the server-selected object's recorded size before signing. Missing/size mismatch is NotReady;
    /// access denied, timeout or storage failure returns 503 PREVIEW_STORAGE_UNAVAILABLE. ETag is not a content hash.
    /// Preview readiness does not grant training/publish permission.</remarks>
    [HttpGet]
    [ProducesResponseType<EditorPreviewResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Get(Guid buildingId, [FromQuery] Guid revisionId, CancellationToken ct)
    {
        var result = await sender.Send(new GetEditorPreviewQuery(User.GetActorId(), buildingId, revisionId), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}
