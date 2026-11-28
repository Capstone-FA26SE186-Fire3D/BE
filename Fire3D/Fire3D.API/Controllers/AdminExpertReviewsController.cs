using Fire3D.API.Authorization;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Expert;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// PlatformAdmin follow-up of thẩm định (BE#242): who each item is assigned to, and reassignment when an expert is away or an item waits
/// too long. PlatformAdmin never decides an item (FR-PLAN-05).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministration)]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminExpertReviewsController(IExpertReviews reviews) : ControllerBase
{
    /// <summary>Thẩm định items with their assignee. kind: Scenario | Infrastructure; status: Submitted (default) | Approved | Rejected;
    /// assigneeId: one expert's items.</summary>
    /// <remarks>Same item shape and order as GET /api/expert/reviews (assigneeId, assigneeName, assignedAt). pageSize 1-100, default 20.</remarks>
    [HttpGet("/api/admin/reviews")]
    [ProducesResponseType<PageResponse<ExpertReviewItem>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> List([FromQuery] string? kind = null, [FromQuery] string? status = null, [FromQuery] Guid? assigneeId = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Respond(await reviews.AdminListAsync(User.GetActorId(), User.GetSessionFamilyId(), new(kind, status, assigneeId, page, pageSize), ct));

    /// <summary>Reassigns a waiting item to another FireSafetyExpert: {expertId, reason (1-500 characters)}. Recorded in the audit log.</summary>
    /// <remarks>400 VALIDATION_ERROR; 404 unknown item; 409 REVIEW_NOT_PENDING (already decided); 422 EXPERT_NOT_AVAILABLE (inactive, locked or
    /// email not verified). Reassigning to the current assignee changes nothing.</remarks>
    [HttpPost("/api/admin/reviews/{id:guid}/reassign")]
    [ProducesResponseType<ExpertReviewItem>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Reassign(Guid id, [FromBody] ExpertReassignRequest request, CancellationToken ct)
        => Respond(await reviews.ReassignAsync(User.GetActorId(), User.GetSessionFamilyId(), id, request ?? new(null, null), ct));

    private IActionResult Respond<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value)
        : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
}
