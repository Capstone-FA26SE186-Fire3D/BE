using Fire3D.API.Authorization;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Expert;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// Thẩm định by FireSafetyExpert (BE#211, FR-PLAN-05). One shared queue for every organization, no assignment: whoever decides first
/// wins and the next expert gets 412 through If-Match. Decisions are final; a fix is a new version and a new submission. Experts read
/// decided items too (read-only). They never read drafts or billing, never edit content and never publish. PlatformAdmin and
/// OrganizationUser get 403 on every route here.
/// </summary>
[ApiController]
[Authorize(Roles = "FireSafetyExpert")]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ExpertReviewsController(IExpertReviews reviews) : ControllerBase
{
    /// <summary>The thẩm định queue. kind: Scenario | Infrastructure (no Infrastructure items until BE#213); status: Submitted (default) | Approved | Rejected.</summary>
    /// <remarks>Submitted items are oldest first; decided items newest decision first. pageSize 1-100, default 20.</remarks>
    [HttpGet("/api/expert/reviews")]
    [ProducesResponseType<PageResponse<ExpertReviewItem>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> List([FromQuery] string? kind = null, [FromQuery] string? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Respond(await reviews.ListAsync(User.GetActorId(), User.GetSessionFamilyId(), new(kind, status, page, pageSize), ct));

    /// <summary>One review with the frozen scenario content and rubric. ETag is the revision to send in If-Match when deciding.</summary>
    [HttpGet("/api/expert/reviews/{id:guid}")]
    [ProducesResponseType<ExpertReviewDetail>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await reviews.GetAsync(User.GetActorId(), User.GetSessionFamilyId(), id, ct);
        if (result.IsSuccess) Response.Headers.ETag = Tag(result.Value!.Review.Revision);
        return Respond(result);
    }

    /// <summary>3D preview (signed GLB, floors, anchors) of the revision the reviewed scenario uses. Same shape as editor-preview.</summary>
    [HttpGet("/api/expert/reviews/{id:guid}/preview")]
    [ProducesResponseType<EditorPreviewResponse>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
        => Respond(await reviews.PreviewAsync(User.GetActorId(), User.GetSessionFamilyId(), id, ct));

    /// <summary>Thẩm định đạt. Needs If-Match (the review ETag) and Idempotency-Key; a replay with the same key returns the first decision.</summary>
    /// <remarks>428 PRECONDITION_REQUIRED (no If-Match); 412 PRECONDITION_FAILED (another expert decided first); 409 CONTENT_REVIEW_NOT_PENDING,
    /// CONTENT_HASH_MISMATCH, IDEMPOTENCY_KEY_CONFLICT. The submitter gets a ContentReviewApproved notification.</remarks>
    [HttpPost("/api/expert/reviews/{id:guid}/approve")]
    [ProducesResponseType<ExpertReviewItem>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    public Task<IActionResult> Approve(Guid id,
        [FromHeader(Name = "If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch,
        [FromHeader(Name = "Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
        => Decide(id, true, null, ifMatch, key, ct);

    /// <summary>Thẩm định chưa đạt, with a reason of 1-2000 characters. Same headers and errors as approve; 400 VALIDATION_ERROR without a reason.</summary>
    /// <remarks>The submitter gets a ContentReviewRejected notification carrying the reason.</remarks>
    [HttpPost("/api/expert/reviews/{id:guid}/reject")]
    [ProducesResponseType<ExpertReviewItem>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    public Task<IActionResult> Reject(Guid id, [FromBody] ExpertRejectRequest request,
        [FromHeader(Name = "If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch,
        [FromHeader(Name = "Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
        => Decide(id, false, request?.Reason, ifMatch, key, ct);

    private async Task<IActionResult> Decide(Guid id, bool approve, string? reason, string? ifMatch, string? key, CancellationToken ct)
    {
        if (!approve && (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000))
            return Error(400, "VALIDATION_ERROR", "A rejection reason of 1-2000 characters is required.",
                new Dictionary<string, string[]> { ["reason"] = ["A rejection reason of 1-2000 characters is required."] });
        if (string.IsNullOrWhiteSpace(key))
            return Error(400, "IDEMPOTENCY_KEY_REQUIRED", "Send an Idempotency-Key header.");
        if (string.IsNullOrWhiteSpace(ifMatch))
            return Error(428, "PRECONDITION_REQUIRED", "Send the review ETag in If-Match.");
        if (!TryRevision(ifMatch, out var expected))
            return Error(400, "VALIDATION_ERROR", "If-Match must contain one quoted positive revision, as returned in ETag.",
                new Dictionary<string, string[]> { ["If-Match"] = ["Use the ETag of GET /api/expert/reviews/{id}."] });
        var result = await reviews.DecideAsync(User.GetActorId(), User.GetSessionFamilyId(), id, approve, reason, expected, key, ct);
        if (result.IsSuccess) Response.Headers.ETag = Tag(result.Value!.Revision);
        return Respond(result);
    }

    private static string Tag(long revision) => $"\"{revision}\"";

    internal static bool TryRevision(string value, out long revision)
    {
        revision = 0;
        var text = value.Trim();
        return text.Length > 2 && text[0] == '"' && text[^1] == '"' && long.TryParse(text[1..^1], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out revision) && revision > 0;
    }

    private IActionResult Respond<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value)
        : Error(result.Error!.Status, result.Error.Code, result.Error.Message, result.Error.Errors);

    private ObjectResult Error(int status, string code, string title, IReadOnlyDictionary<string, string[]>? errors = null)
        => Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code, ["errors"] = errors });
}
