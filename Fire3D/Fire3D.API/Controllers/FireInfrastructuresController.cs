using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.FireSafety;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// Fire infrastructure of an IFC revision (BE#213, FR-PLAN-04): equipment, exits, assembly points and water sources, kept apart from
/// scenarios. Draft -> Submitted -> Approved (locked) | Rejected; only a Draft is edited, and a fix after a decision is a new version
/// copied from the newest one. A FireSafetyExpert decides through /api/expert/reviews (kind Infrastructure). Scenario drafts are
/// created on an Approved version (POST /api/scenarios/{id}/draft with infrastructureId). OrganizationUser of the Building or PlatformAdmin.
/// </summary>
[ApiController]
[Authorize(Roles = "OrganizationUser,PlatformAdmin")]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FireInfrastructuresController(IFireInfrastructureService infrastructures) : ControllerBase
{
    /// <summary>Every fire infrastructure version of the revision, newest first.</summary>
    [HttpGet("/api/revisions/{revisionId:guid}/fire-infrastructures")]
    [ProducesResponseType<FireInfrastructureListResponse>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> List(Guid revisionId, CancellationToken ct)
        => Respond(await infrastructures.ListAsync(User.GetActorId(), User.GetSessionFamilyId(), revisionId, ct));

    /// <summary>Starts a new Draft version. Needs Idempotency-Key.</summary>
    /// <remarks>The first version is seeded from the revision's Confirmed/Retyped equipment decisions (fixed equipment and water sources at
    /// their detected position, capability versions of the newest active runtime); exits and assembly points are added by PUT. Later
    /// versions copy the newest version. 409 FIRE_INFRASTRUCTURE_OPEN_VERSION_EXISTS (errors.id) while a Draft or Submitted version exists.</remarks>
    [HttpPost("/api/revisions/{revisionId:guid}/fire-infrastructures")]
    [ProducesResponseType<FireInfrastructureResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Create(Guid revisionId,
        [FromHeader(Name = "Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await infrastructures.CreateAsync(User.GetActorId(), User.GetSessionFamilyId(), revisionId, key, ct);
        if (!result.IsSuccess) return Error(result.Error!);
        Response.Headers.ETag = Tag(result.Value!.Revision);
        return Created($"/api/fire-infrastructures/{result.Value.Id}", result.Value);
    }

    /// <summary>One version with its items. ETag is the revision for If-Match.</summary>
    [HttpGet("/api/fire-infrastructures/{id:guid}")]
    [ProducesResponseType<FireInfrastructureResponse>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await infrastructures.GetAsync(User.GetActorId(), User.GetSessionFamilyId(), id, ct);
        if (result.IsSuccess) Response.Headers.ETag = Tag(result.Value!.Revision);
        return Respond(result);
    }

    /// <summary>Replaces geometry, runtimeVersion and items of a Draft. Needs If-Match.</summary>
    /// <remarks>items[]: fet3d.editor/1 objects plus category Equipment | WaterSource (kind Equipment, fixed capability) or Exit |
    /// AssemblyPoint (kind Goal). Shape problems: 422 FIRE_INFRASTRUCTURE_INVALID with issues[{code,path,message}]. 409
    /// FIRE_INFRASTRUCTURE_LOCKED on an Approved version, FIRE_INFRASTRUCTURE_NOT_DRAFT on a Submitted or Rejected one; 412/428 for If-Match.</remarks>
    [HttpPut("/api/fire-infrastructures/{id:guid}")]
    [ProducesResponseType<FireInfrastructureResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(428)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateFireInfrastructureRequest request,
        [FromHeader(Name = "If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        if (Precondition(ifMatch, out var expected) is { } problem) return problem;
        var result = await infrastructures.UpdateAsync(User.GetActorId(), User.GetSessionFamilyId(), id, expected, request ?? new(null, null, null), ct);
        if (result.IsSuccess) Response.Headers.ETag = Tag(result.Value!.Revision);
        return Respond(result);
    }

    /// <summary>Dry run of the submit checks on the stored content: item rules, capabilities of runtimeVersion, pinned accepted Geometry,
    /// floors and anchors, at least one Exit.</summary>
    [HttpPost("/api/fire-infrastructures/{id:guid}/validate")]
    [ProducesResponseType<FireInfrastructureValidationResponse>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct)
        => Respond(await infrastructures.ValidateAsync(User.GetActorId(), User.GetSessionFamilyId(), id, ct));

    /// <summary>Sends a Draft to the FireSafetyExpert queue. Needs If-Match and Idempotency-Key.</summary>
    /// <remarks>422 FIRE_INFRASTRUCTURE_INVALID with issues when validate would fail. The submitter gets FireInfrastructureApproved or
    /// FireInfrastructureRejected (with the reason) as a web notification when an expert decides.</remarks>
    [HttpPost("/api/fire-infrastructures/{id:guid}/submit")]
    [ProducesResponseType<FireInfrastructureResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(428)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Submit(Guid id,
        [FromHeader(Name = "If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch,
        [FromHeader(Name = "Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return Error(new AuthError("IDEMPOTENCY_KEY_REQUIRED", "Send an Idempotency-Key header.", 400));
        if (Precondition(ifMatch, out var expected) is { } problem) return problem;
        var result = await infrastructures.SubmitAsync(User.GetActorId(), User.GetSessionFamilyId(), id, expected, key, ct);
        if (result.IsSuccess) Response.Headers.ETag = Tag(result.Value!.Revision);
        return Respond(result);
    }

    private IActionResult? Precondition(string? ifMatch, out long expected)
    {
        expected = 0;
        if (string.IsNullOrWhiteSpace(ifMatch)) return Error(new AuthError("PRECONDITION_REQUIRED", "Send the fire infrastructure ETag in If-Match.", 428));
        if (!ExpertReviewsController.TryRevision(ifMatch, out expected))
            return Error(new AuthError("VALIDATION_ERROR", "If-Match must contain one quoted positive revision, as returned in ETag.", 400,
                new Dictionary<string, string[]> { ["If-Match"] = ["Use the ETag of GET /api/fire-infrastructures/{id}."] }));
        return null;
    }

    private static string Tag(long revision) => $"\"{revision}\"";

    private IActionResult Respond<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value) : Error(result.Error!);

    private ObjectResult Error(AuthError error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors };
        if (error.Issues is not null) extensions["issues"] = error.Issues;
        return Problem(statusCode: error.Status, title: error.Message, extensions: extensions);
    }
}
