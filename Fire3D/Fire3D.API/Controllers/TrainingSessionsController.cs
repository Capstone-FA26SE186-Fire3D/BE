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

    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpGet("sessions/{id:guid}")]
    [ProducesResponseType<TrainingSessionView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Sync(id) is { } caller ? Reply(await sessions.GetAsync(caller, id, ct)) : Forbid();

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
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Launched(Guid id, LaunchedTrainingSessionRequest request, CancellationToken ct) => Reply(await sessions.LaunchedAsync(Live(), id, request, ct));

    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/heartbeat")]
    [ProducesResponseType<HeartbeatResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Heartbeat(Guid id, CancellationToken ct) => Sync(id) is { } caller ? Reply(await sessions.HeartbeatAsync(caller, id, ct)) : Forbid();

    /// <summary>Append-only telemetry: same eventId and content replays; changed content or reused sequence is a per-event conflict. Returns acknowledged sequence and gaps.</summary>
    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/events:batch")]
    [ProducesResponseType<TrainingEventsResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Events(Guid id, TrainingEventsRequest request, CancellationToken ct) => Sync(id) is { } caller ? Reply(await sessions.RecordEventsAsync(caller, id, request, ct)) : Forbid();

    /// <summary>Pins lastEventSequence. 200 Completed with one immutable result, or 202 AwaitingSync with missing ranges until every event arrives.</summary>
    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpPost("sessions/{id:guid}/complete")]
    [ProducesResponseType<CompleteTrainingSessionResponse>(200)]
    [ProducesResponseType<CompleteTrainingSessionResponse>(202)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Complete(Guid id, CompleteTrainingSessionRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        if (Sync(id) is not { } caller) return Forbid();
        var result = await sessions.CompleteAsync(caller, id, request, key, ct);
        return result.IsSuccess ? StatusCode(result.Value.Status, result.Value.Body) : Problem(result.Error!);
    }

    [Authorize(AuthenticationSchemes = SyncSchemes, Roles = "Trainee")]
    [HttpGet("sessions/{id:guid}/result")]
    [ProducesResponseType<TrainingResultView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Result(Guid id, CancellationToken ct) => Sync(id) is { } caller ? Reply(await sessions.ResultAsync(caller, id, ct)) : Forbid();

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
}

/// <summary>Building QR: Organization/Admin manage codes; signed-in users resolve a token to its Building and list Trainings with normal access checks.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BuildingQrController(IBuildingQrCodes qr, ISender sender) : ControllerBase
{
    [Authorize(Roles = "OrganizationUser,PlatformAdmin")]
    [HttpPost("api/buildings/{buildingId:guid}/qr-codes")]
    [ProducesResponseType<BuildingQrCreated>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Create(Guid buildingId, CreateBuildingQrRequest request, CancellationToken ct)
    {
        var result = await qr.CreateAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, request, ct);
        return result.IsSuccess ? Created($"/api/buildings/{buildingId}/qr-codes", result.Value) : Problem(result.Error!);
    }
    [Authorize(Roles = "OrganizationUser,PlatformAdmin")]
    [HttpGet("api/buildings/{buildingId:guid}/qr-codes")]
    [ProducesResponseType<IReadOnlyList<BuildingQrView>>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> List(Guid buildingId, CancellationToken ct) => Reply(await qr.ListAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, ct));
    [Authorize(Roles = "OrganizationUser,PlatformAdmin")]
    [HttpPost("api/buildings/{buildingId:guid}/qr-codes/{qrId:guid}/rotate")]
    [ProducesResponseType<BuildingQrCreated>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Rotate(Guid buildingId, Guid qrId, CreateBuildingQrRequest request, CancellationToken ct) => Reply(await qr.RotateAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, qrId, request, ct));
    [Authorize(Roles = "OrganizationUser,PlatformAdmin")]
    [HttpPost("api/buildings/{buildingId:guid}/qr-codes/{qrId:guid}/revoke")]
    [ProducesResponseType<BuildingQrView>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Revoke(Guid buildingId, Guid qrId, CancellationToken ct) => Reply(await qr.RevokeAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, qrId, ct));
    [Authorize(Roles = "Trainee,OrganizationUser,PlatformAdmin")]
    [HttpGet("api/qr/{qrToken}")]
    [ProducesResponseType<BuildingQrResolution>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Resolve(string qrToken, CancellationToken ct) => Reply(await qr.ResolveAsync(User.GetActorId(), User.GetSessionFamilyId(), qrToken, ct));
    /// <summary>Published Trainings of the QR Building; Private Buildings still require the Trainee's participation grant.</summary>
    [Authorize(Roles = "Trainee,OrganizationUser,PlatformAdmin")]
    [HttpGet("api/qr/{qrToken}/trainings")]
    [ProducesResponseType<List<Fire3D.Application.Buildings.Queries.GetTrainings.TrainingDto>>(200)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Trainings(string qrToken, CancellationToken ct)
    {
        var resolved = await qr.ResolveAsync(User.GetActorId(), User.GetSessionFamilyId(), qrToken, ct);
        if (!resolved.IsSuccess) return Problem(resolved.Error!);
        return Reply(await sender.Send(new Fire3D.Application.Buildings.Queries.GetTrainings.GetTrainingsQuery(User.GetActorId(), resolved.Value!.BuildingId, User.GetSessionFamilyId()), ct));
    }
    private IActionResult Reply<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    private ObjectResult Problem(AuthError error) => Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
}
