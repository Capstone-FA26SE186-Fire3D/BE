using System.Globalization;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>Material style of a revision (STYLE-01): a published StylePreset, an optional ColorScheme and material overrides.</summary>
[ApiController]
[Authorize(Roles = "OrganizationUser,PlatformAdmin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Produces("application/json")]
public sealed class RevisionStyleController(IRevisionStyles styles) : ContentControllerBase
{
    /// <summary>The revision's current style selection and its ETag.</summary>
    /// <remarks>version 0 and selection null before the first save. previousSelection is the newest earlier revision's choice for the same
    /// Building, shown as a suggestion only. Presets and schemes come from GET /api/library/items?kind=StylePreset|ColorScheme.
    /// Another tenant: 404.</remarks>
    [HttpGet("api/revisions/{revisionId:guid}/style")]
    [ProducesResponseType<RevisionStyleView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Get(Guid revisionId, CancellationToken ct) => Respond(await styles.GetAsync(Actor, Family, revisionId, ct));

    /// <summary>Saves a new style selection with If-Match; earlier versions stay unchanged so pinned builds keep theirs.</summary>
    /// <remarks>Body {presetVersionId, schemeVersionId?, overrides?:[{room, surface: floor|wall|ceiling, material}]}, at most 200 overrides,
    /// one per room and surface; room "default" applies to every room. Malformed body 422 CONTENT_INVALID; preset or scheme not a published
    /// version of an active Library item of that kind 422 STYLE_SELECTION_INVALID; superseded/rejected revision 409 REVISION_NOT_EDITABLE.
    /// Missing If-Match 428, malformed 400, stale 412. Audited.</remarks>
    [HttpPut("api/revisions/{revisionId:guid}/style")]
    [ProducesResponseType<RevisionStyleView>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Save(Guid revisionId, RevisionStyleRequest request,
        [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            return Problem(statusCode: 428, title: "If-Match is required.", extensions: new Dictionary<string, object?> { ["code"] = "PRECONDITION_REQUIRED" });
        var tag = ifMatch.Trim();
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"' || !long.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var expected))
            return Problem(statusCode: 400, title: "If-Match must be the quoted version from GET.", extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
        var issues = StyleRules.Validate(request);
        if (issues.Count > 0) return Invalid(issues);
        return Respond(await styles.SaveAsync(Actor, Family, revisionId, expected, request, ct));
    }

    private IActionResult Respond(Fire3D.Application.Authentication.AuthResult<RevisionStyleView> result)
    {
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
        Response.Headers.ETag = $"\"{result.Value!.Version.ToString(CultureInfo.InvariantCulture)}\"";
        return Ok(result.Value);
    }
}
