using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// Playtest status, Mobile handoff and runtime lifecycle for the OrganizationUser who prepared the playtest.
/// Every action re-reads the live session family, owner and tenant. 401 invalid session, 403 role, 404 outside scope,
/// 409 state/precondition conflict, 422 invalid content, 503 feature not configured. Playtests never count learner seats or analytics.
/// </summary>
[ApiController]
[Route("api/playtests")]
[Authorize(Roles = "OrganizationUser")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PlaytestsController(IPlaytestLifecycle playtests) : ControllerBase
{
    /// <summary>Status, pinned package, current grant generation/expiry and which recovery actions the calling session may perform.</summary>
    [HttpGet("{playtestId:guid}")]
    [ProducesResponseType<PlaytestStatusResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid playtestId, CancellationToken ct) =>
        Reply(await playtests.GetAsync(User.GetActorId(), User.GetSessionFamilyId(), playtestId, ct));

    /// <summary>Creates a five-minute single-use code for QR/deep link. A new code revokes earlier open codes. Only before start.</summary>
    /// <remarks>The code is 32 random bytes (base64url); only its hash is stored, plus a short-lived ciphertext so a retry with the
    /// same Idempotency-Key returns the same code. Requires Playtest:HandoffKey and HandoffDeepLinkBaseUrl, otherwise 503.</remarks>
    [HttpPost("{playtestId:guid}/handoffs")]
    [ProducesResponseType<PlaytestHandoffResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> CreateHandoff(Guid playtestId, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await playtests.CreateHandoffAsync(User.GetActorId(), User.GetSessionFamilyId(), playtestId, key, ct);
        return result.IsSuccess ? Created($"/api/playtests/{playtestId}", result.Value) : Problem(result.Error!);
    }

    /// <summary>Redeems a handoff code from another session (Mobile) of the same OrganizationUser and binds the playtest to it.</summary>
    /// <remarks>The issuing Web session must still be valid. Same session retry returns the current status; another user's code is
    /// 404 and a code redeemed by another session is 409 PLAYTEST_HANDOFF_USED. A started playtest cannot move to another session.</remarks>
    [HttpPost("handoffs/redeem")]
    [ProducesResponseType<PlaytestStatusResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Redeem([FromBody] RedeemPlaytestHandoffRequest request, CancellationToken ct) =>
        Reply(await playtests.RedeemHandoffAsync(User.GetActorId(), User.GetSessionFamilyId(), request, ct));

    /// <summary>Reissues the five-minute launch grant before the runtime confirms launch; increments generation and fences older grants.</summary>
    /// <remarks>Only the launching session, only in Launching. Rechecks live session, accepted package, runtime compatibility and the
    /// entitlement used at start; never consumes another Trial unit.</remarks>
    [HttpPost("{playtestId:guid}/launch-grants")]
    [ProducesResponseType<PlaytestLaunch>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> ReissueGrant(Guid playtestId, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct) =>
        Reply(await playtests.ReissueGrantAsync(User.GetActorId(), User.GetSessionFamilyId(), playtestId, key, ct));

    /// <summary>Runtime confirms it received the session for the current, unexpired grant generation: Launching → Running.</summary>
    [HttpPost("{playtestId:guid}/launched")]
    [ProducesResponseType<PlaytestStatusResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Launched(Guid playtestId, [FromBody] LaunchedPlaytestRequest request, CancellationToken ct) =>
        Reply(await playtests.LaunchedAsync(User.GetActorId(), User.GetSessionFamilyId(), playtestId, request, ct));

    /// <summary>Liveness for a Running playtest from the launching session; stores server-received time.</summary>
    [HttpPost("{playtestId:guid}/heartbeat")]
    [ProducesResponseType<PlaytestHeartbeatResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Heartbeat(Guid playtestId, CancellationToken ct) =>
        Reply(await playtests.HeartbeatAsync(User.GetActorId(), User.GetSessionFamilyId(), playtestId, ct));

    /// <summary>Durable append-only telemetry. Same eventId and content replays; a different hash or reused sequence is a per-event conflict.</summary>
    [HttpPost("{playtestId:guid}/events:batch")]
    [ProducesResponseType<PlaytestEventsResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Events(Guid playtestId, [FromBody] PlaytestEventsRequest request, CancellationToken ct) =>
        Reply(await playtests.RecordEventsAsync(User.GetActorId(), User.GetSessionFamilyId(), playtestId, request, ct));

    /// <summary>Completes once after events 1..lastEventSequence are stored; same key and input replays, otherwise 409.</summary>
    [HttpPost("{playtestId:guid}/complete")]
    [ProducesResponseType<PlaytestStatusResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Complete(Guid playtestId, [FromBody] CompletePlaytestRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct) =>
        Reply(await playtests.CompleteAsync(User.GetActorId(), User.GetSessionFamilyId(), playtestId, request, key, ct));

    /// <summary>Ends the playtest as Cancelled from any live session of the owner; serialised with complete. Trial is not refunded.</summary>
    [HttpPost("{playtestId:guid}/cancel")]
    [ProducesResponseType<PlaytestStatusResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Cancel(Guid playtestId, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct) =>
        Reply(await playtests.CancelAsync(User.GetActorId(), User.GetSessionFamilyId(), playtestId, key, ct));

    private IActionResult Reply<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    private ObjectResult Problem(AuthError error) => Problem(statusCode: error.Status, title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
}
