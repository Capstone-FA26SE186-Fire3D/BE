using System.Globalization;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>Equipment detected from IFC (IFC-04): candidates of the accepted geometry and OrganizationUser decisions on them.</summary>
[ApiController]
[Authorize(Roles = "OrganizationUser,PlatformAdmin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Produces("application/json")]
public sealed class EquipmentCandidatesController(IEquipmentCandidates candidates) : ContentControllerBase
{
    /// <summary>Candidates the IFC worker detected in the revision's accepted geometry, each with its latest decision and an ETag.</summary>
    /// <remarks>Candidates come from the building_metadata artifact of the accepted Geometry attempt (Passed QA, no blocking issue);
    /// needsConfirmation is true below confidence 0.9. A candidate without a decision may carry suggestedDecision from the newest
    /// earlier revision of the Building with the same ifcGlobalId; it is not applied until saved. No accepted geometry: 409
    /// EQUIPMENT_CANDIDATES_UNAVAILABLE. The worker sent no building_metadata: metadataAvailable=false and no items. Another tenant: 404.</remarks>
    [HttpGet("api/revisions/{revisionId:guid}/equipment-candidates")]
    [ProducesResponseType<EquipmentCandidatesView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Get(Guid revisionId, CancellationToken ct) => Respond(await candidates.GetAsync(Actor, Family, revisionId, ct));

    /// <summary>Confirms, retypes or rejects candidates with If-Match; decisions merge over the latest ones.</summary>
    /// <remarks>Body {items:[{candidateId, decision: Confirmed|Retyped|Rejected, catalogKey?, note?}]}, 1-500 items. Confirmed keeps the
    /// detected catalogKey unless one is sent; Retyped needs catalogKey; Rejected takes none. A catalogKey must be an Equipment capability
    /// of an active runtime (GET /api/scenario-interactions/catalog). Missing If-Match 428, stale 412, unknown candidate or catalogKey 422
    /// EQUIPMENT_DECISION_INVALID. Decisions never change revision artifacts; the editor turns a confirmed candidate into an Equipment
    /// object (anchorId = ifcGlobalId, capability = catalogKey).</remarks>
    [HttpPatch("api/revisions/{revisionId:guid}/equipment-decisions")]
    [ProducesResponseType<EquipmentCandidatesView>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Save(Guid revisionId, EquipmentDecisionsRequest request,
        [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            return Problem(statusCode: 428, title: "If-Match is required.", extensions: new Dictionary<string, object?> { ["code"] = "PRECONDITION_REQUIRED" });
        var tag = ifMatch.Trim();
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"' || !long.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var expected))
            return Problem(statusCode: 400, title: "If-Match must be the quoted version from GET.", extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
        var issues = EquipmentDecisionRules.Validate(request);
        if (issues.Count > 0) return Invalid(issues);
        return Respond(await candidates.SaveAsync(Actor, Family, revisionId, expected, request, ct));
    }

    private IActionResult Respond(Fire3D.Application.Authentication.AuthResult<EquipmentCandidatesView> result)
    {
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
        Response.Headers.ETag = $"\"{result.Value!.Version.ToString(CultureInfo.InvariantCulture)}\"";
        return Ok(result.Value);
    }
}
