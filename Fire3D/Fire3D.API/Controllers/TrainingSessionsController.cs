using System.Security.Claims;
using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Learning;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// Learner session lifecycle for Trainees. Prepare pins Training/release/version/rubric/package/runtime without a seat; start
/// rechecks access, approval, entitlement and runtime online and allocates one seat per Trainee per entitlement period. Sync
/// endpoints also accept the session's continuation token (7-day offline window), which never prepares or starts anything.
/// </summary>
[ApiController]
[Route("api/training")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TrainingSessionsController(ILearnerSessions sessions) : ControllerBase
{
    public const string SyncSchemes = JwtBearerDefaults.AuthenticationScheme + "," + LearnerContinuationAuthentication.SchemeName;

    private LearnerCaller Live() => new(User.GetActorId(), User.GetSessionFamilyId(), null);
    // Continuation callers are bound to the one session named in the token.
    private LearnerCaller? Sync(Guid session)
    {
        if (User.FindFirstValue("purpose") != "learner_continuation") return Live();
        return User.FindFirstValue("session_id") == session.ToString() && Guid.TryParse(User.FindFirstValue("jti"), out var id) ? new(User.GetActorId(), null, id) : null;
    }

    [Authorize(Roles = "Trainee")]
    [HttpPost("sessions")]
    [ProducesResponseType<TrainingSessionView>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Prepare(PrepareTrainingSessionRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await sessions.PrepareAsync(Live(), request, key, ct);
        return result.IsSuccess ? Created($"/api/training/sessions/{result.Value!.Id}", result.Value) : Problem(result.Error!);
    }

    /// <summary>The signed-in Trainee's own training history, newest first (startedAt desc, then id desc).</summary>
    /// <remarks>
    /// Live login only (a continuation token cannot list). Lists started sessions only: preparations and Organization playtests
    /// never appear, and other users' sessions are never visible. Losing Building access later does not hide past sessions.
    /// Filters: status (Launching, Running, AwaitingSync, Completed — the only states v7 sessions take), mode (Learn, Guided,
    /// Assessment), buildingId (see GET /api/training/buildings), from/to on startedAt (from inclusive, to exclusive). Session status
    /// is not a pass: resultStatus is Passed, NotPassed, Incomplete or NotAssessed once the server stored a result, otherwise null.
    /// endReason Abandoned is how an abandoned play ends; it is not a status. Open the result with GET /api/training/sessions/{id}/result.
    /// page >= 1, pageSize 1-100 (default 20); invalid filters return 400 VALIDATION_ERROR with errors per field.
    /// </remarks>
    [Authorize(Roles = "Trainee")]
    [HttpGet("sessions")]
    [ProducesResponseType<TrainingHistoryPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<IActionResult> List([FromQuery, OpenApi.EnumParameter("Launching", "Running", "AwaitingSync", "Completed")] string? status,
        [FromQuery, OpenApi.EnumParameter("Learn", "Guided", "Assessment")] string? mode, [FromQuery] Guid? buildingId,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Reply(await sessions.ListAsync(Live(), new(status, mode, buildingId, from, to, page, pageSize), ct));

    /// <summary>Buildings the signed-in Trainee has played in, most recent session first, for the history Building filter.</summary>
    /// <remarks>Live login only. Built from the Trainee's own started sessions; losing access later does not remove a Building.
    /// page >= 1, pageSize 1-100 (default 20).</remarks>
    [Authorize(Roles = "Trainee")]
    [HttpGet("buildings")]
    [ProducesResponseType<TrainingBuildingPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<IActionResult> Buildings([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Reply(await sessions.BuildingsAsync(Live(), page, pageSize, ct));

    /// <summary>A preparation (Prepared, Expired) or a started session of the caller. Accepts the session's continuation token.</summary>
    /// <remarks>403 CONTINUATION_SESSION_MISMATCH when a continuation token is used for another session id.</remarks>
    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpGet("sessions/{id:guid}")]
    [ProducesResponseType<TrainingSessionView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Sync(id) is { } caller ? Reply(await sessions.GetAsync(caller, id, ct)) : Mismatch();

    /// <summary>Online start: live session, lifecycle, Public/Private access, approval, Published release, entitlement, runtime and seat; returns a five-minute launch grant and a continuation token. Before launch the same session may call start again for a new grant generation.</summary>
    [Authorize(Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/start")]
    [ProducesResponseType<StartTrainingSessionResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Start(Guid id, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct) =>
        Reply(await sessions.StartAsync(Live(), id, key, ct));

    [Authorize(Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/launched")]
    [ProducesResponseType<TrainingSessionView>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Launched(Guid id, LaunchedTrainingSessionRequest request, CancellationToken ct) => Reply(await sessions.LaunchedAsync(Live(), id, request, ct));

    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/heartbeat")]
    [ProducesResponseType<HeartbeatResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Heartbeat(Guid id, CancellationToken ct) => Sync(id) is { } caller ? Reply(await sessions.HeartbeatAsync(caller, id, ct)) : Mismatch();

    /// <summary>Append-only telemetry: same eventId and content replays; changed content or reused sequence is a per-event conflict. Returns acknowledged sequence and gaps.</summary>
    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/events:batch")]
    [ProducesResponseType<TrainingEventsResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Events(Guid id, TrainingEventsRequest request, CancellationToken ct) => Sync(id) is { } caller ? Reply(await sessions.RecordEventsAsync(caller, id, request, ct)) : Mismatch();

    /// <summary>Pins lastEventSequence. 200 Completed with one immutable result, or 202 AwaitingSync with missing ranges until every event arrives.</summary>
    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/complete")]
    [ProducesResponseType<CompleteTrainingSessionResponse>(200)]
    [ProducesResponseType<CompleteTrainingSessionResponse>(202)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Complete(Guid id, CompleteTrainingSessionRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        if (Sync(id) is not { } caller) return Mismatch();
        var result = await sessions.CompleteAsync(caller, id, request, key, ct);
        return result.IsSuccess ? StatusCode(result.Value.Status, result.Value.Body) : Problem(result.Error!);
    }

    /// <summary>The server-computed result of the caller's session. Accepts the session's continuation token.</summary>
    /// <remarks>409 RESULT_NOT_READY until every event up to completion arrived (also for a preparation id). Criteria are listed for
    /// Assessment only, each with the rubric label/description; reason and criteria[].reason are machine codes (see the schema).
    /// 403 CONTINUATION_SESSION_MISMATCH when a continuation token is used for another session id; 403 FORBIDDEN for non-Trainee accounts.</remarks>
    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpGet("sessions/{id:guid}/result")]
    [ProducesResponseType<TrainingResultView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Result(Guid id, CancellationToken ct) => Sync(id) is { } caller ? Reply(await sessions.ResultAsync(caller, id, ct)) : Mismatch();

    /// <summary>Live login reissues the continuation for the same owner and started session; the previous token stops working.</summary>
    [Authorize(Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/continuation")]
    [ProducesResponseType<TrainingContinuation>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Continuation(Guid id, CancellationToken ct) => Reply(await sessions.ContinuationAsync(Live(), id, ct));

    /// <summary>Reports the caller's own sessions for given IDs or prepare/start idempotency keys; other users' IDs are simply absent.</summary>
    [Authorize(Roles = "Trainee")]
    [HttpPost("reconcile")]
    [ProducesResponseType<IReadOnlyList<TrainingSessionView>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    public async Task<IActionResult> Reconcile(ReconcileTrainingRequest request, CancellationToken ct) => Reply(await sessions.ReconcileAsync(Live(), request, ct));

    private IActionResult Reply<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    private ObjectResult Problem(AuthError error) => Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
    // A continuation token only syncs the session it was issued for.
    private ObjectResult Mismatch() => Problem(new AuthError("CONTINUATION_SESSION_MISMATCH", "This continuation token belongs to another session.", 403));
}

/// <summary>Building QR: Organization/Admin manage codes; signed-in users resolve a token to its Building and list Trainings with normal access checks.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BuildingQrController(IBuildingQrCodes qr, ISender sender) : ControllerBase
{
    /// <summary>Creates the canonical QR code of a Building.</summary>
    /// <remarks>A Building has one Active code; a second create returns 409 QR_ALREADY_EXISTS, so a retried create never adds another code. Use rotate to replace it.</remarks>
    [Authorize(Roles = "OrganizationUser,PlatformAdmin")]
    [HttpPost("api/buildings/{buildingId:guid}/qr-codes")]
    [ProducesResponseType<BuildingQrCreated>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Create(Guid buildingId, CreateBuildingQrRequest request, CancellationToken ct)
    {
        var result = await qr.CreateAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, request, ct);
        return result.IsSuccess ? Created($"/api/buildings/{buildingId}/qr-codes", result.Value) : Problem(result.Error!);
    }
    [Authorize(Roles = "OrganizationUser,PlatformAdmin")]
    [HttpGet("api/buildings/{buildingId:guid}/qr-codes")]
    [ProducesResponseType<BuildingQrPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> List(Guid buildingId, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Reply(await qr.ListAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, page, pageSize, ct));
    [Authorize(Roles = "OrganizationUser,PlatformAdmin")]
    [HttpPost("api/buildings/{buildingId:guid}/qr-codes/{qrId:guid}/rotate")]
    [ProducesResponseType<BuildingQrCreated>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Rotate(Guid buildingId, Guid qrId, CreateBuildingQrRequest request, CancellationToken ct) => Reply(await qr.RotateAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, qrId, request, ct));
    [Authorize(Roles = "OrganizationUser,PlatformAdmin")]
    [HttpPost("api/buildings/{buildingId:guid}/qr-codes/{qrId:guid}/revoke")]
    [ProducesResponseType<BuildingQrView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Revoke(Guid buildingId, Guid qrId, CancellationToken ct) => Reply(await qr.RevokeAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, qrId, ct));
    [Authorize(Roles = "Trainee,OrganizationUser,PlatformAdmin")]
    [HttpGet("api/qr/{qrToken}")]
    [ProducesResponseType<BuildingQrResolution>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(410)]
    public async Task<IActionResult> Resolve(string qrToken, CancellationToken ct) => Reply(await qr.ResolveAsync(User.GetActorId(), User.GetSessionFamilyId(), qrToken, ct));
    /// <summary>Published Trainings of the QR Building; Private Buildings still require the Trainee's participation grant.</summary>
    [Authorize(Roles = "Trainee,OrganizationUser,PlatformAdmin")]
    [HttpGet("api/qr/{qrToken}/trainings")]
    [ProducesResponseType<List<Fire3D.Application.Buildings.Queries.GetTrainings.TrainingDto>>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(410)]
    public async Task<IActionResult> Trainings(string qrToken, CancellationToken ct)
    {
        var resolved = await qr.ResolveAsync(User.GetActorId(), User.GetSessionFamilyId(), qrToken, ct);
        if (!resolved.IsSuccess) return Problem(resolved.Error!);
        return Reply(await sender.Send(new Fire3D.Application.Buildings.Queries.GetTrainings.GetTrainingsQuery(User.GetActorId(), resolved.Value!.BuildingId, User.GetSessionFamilyId()), ct));
    }
    private IActionResult Reply<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    private ObjectResult Problem(AuthError error) => Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
}
