using System.Text.Json;
using Fire3D.Application.Authentication;

namespace Fire3D.Application.Learning;

/// <summary>Learner launch and offline continuation signing. Disabled by default; prepare works without it.</summary>
public sealed class LearnerSessionOptions
{
    public bool Enabled { get; set; }
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "FET3D.Learner";
    public string Audience { get; set; } = "FET3D.Runtime.Learner";
    public string ContinuationSigningKey { get; set; } = "";
    public string ContinuationIssuer { get; set; } = "FET3D.Learner";
    public string ContinuationAudience { get; set; } = "FET3D.Learner.Continuation";
    /// <summary>Offline sync window for a started session; after it a live login reissues continuation for the same owner.</summary>
    public int ContinuationTtlDays { get; set; } = 7;
}

/// <summary>Who is calling: a live Fire3D session (Family) or a continuation for exactly one started session.</summary>
public sealed record LearnerCaller(Guid ActorId, Guid? FamilyId, Guid? ContinuationId);

public sealed record PrepareTrainingSessionRequest(Guid TrainingId, string Mode, string RuntimeVersion);
public sealed record LaunchedTrainingSessionRequest(int Generation);
/// <summary>
/// ElapsedMs is time since launch. Metric events: ExitReached, WrongExit, HazardExposure {amount ≥ 0}, Moved {distanceMeters ≥ 0}.
/// </summary>
public sealed record TrainingEvent(Guid EventId, long Sequence, string SchemaVersion, string Type, DateTimeOffset OccurredAt, long ElapsedMs, JsonElement Payload);
public sealed record TrainingEventsRequest(IReadOnlyList<TrainingEvent> Events);
/// <summary>EndReason: Finished, TimedOut (rubric evaluated) or Abandoned (Assessment outcome Incomplete).</summary>
public sealed record CompleteTrainingSessionRequest(long LastEventSequence, string EndReason);
public sealed record ReconcileTrainingRequest(IReadOnlyList<Guid>? SessionIds = null, IReadOnlyList<string>? IdempotencyKeys = null);

public sealed record SessionGrantView(int Generation, DateTime IssuedAt, DateTime ExpiresAt);
public sealed record SessionCompletionView(long LastEventSequence, string EndReason, DateTime RequestedAt);
/// <summary>
/// Status: Prepared (no seat, not a play) → Launching (started online, seat held) → Running → AwaitingSync (completion waits for
/// missing events) → Completed (one immutable server result). Expired preparations must be prepared again.
/// </summary>
public sealed record TrainingSessionView(Guid Id, string Status, Guid TrainingId, Guid ReleaseId, Guid ScenarioVersionId, Guid BuildingId, string Mode,
    string RuntimeVersion, string ScenarioHash, string? RubricHash, JsonElement Package, DateTime? PreparedAt, DateTime? ExpiresAt, DateTime? StartedAt,
    DateTime? LaunchedAt, DateTime? EndedAt, DateTime? LastHeartbeatAt, SessionGrantView? Grant, bool? LaunchSession, DateTime? ContinuationExpiresAt,
    long? AcknowledgedSequence, SessionCompletionView? Completion, bool HasResult);
public sealed record TrainingLaunch(Guid SessionId, int Generation, DateTime IssuedAt, DateTime ExpiresAt, string LaunchGrant);
public sealed record TrainingContinuation(Guid SessionId, DateTime ExpiresAt, string Token);
public sealed record StartTrainingSessionResponse(TrainingSessionView Session, TrainingLaunch Launch, TrainingContinuation Continuation);
public sealed record TrainingEventConflict(Guid EventId, string Code);
public sealed record TrainingSequenceRange(long From, long To);
public sealed record TrainingEventsResponse(IReadOnlyList<Guid> Accepted, IReadOnlyList<Guid> Duplicates, IReadOnlyList<TrainingEventConflict> Conflicts,
    string Status, long AcknowledgedSequence, IReadOnlyList<TrainingSequenceRange> MissingRanges);
public sealed record CompleteTrainingSessionResponse(string Status, TrainingSessionView Session, long AcknowledgedSequence, IReadOnlyList<TrainingSequenceRange> MissingRanges);
public sealed record CriterionResult(string Id, string Metric, JsonElement? Value, string Operator, decimal Threshold, bool Mandatory, decimal Weight, bool Passed, string? Reason);
/// <summary>Outcome Passed/NotPassed/Incomplete for Assessment, NotAssessed for Learn/Guided. Score is a server-computed percent; completion is not Passed.</summary>
public sealed record TrainingResultView(Guid SessionId, string Mode, string Outcome, string Reason, decimal? ScorePercent, string? RubricHash,
    IReadOnlyList<CriterionResult>? Criteria, JsonElement Metrics, long LastEventSequence, DateTime CreatedAt);
public sealed record HeartbeatResponse(DateTime ServerTime, string Status);

public interface ILearnerSessions
{
    Task<AuthResult<TrainingSessionView>> PrepareAsync(LearnerCaller caller, PrepareTrainingSessionRequest request, string? key, CancellationToken ct);
    Task<AuthResult<TrainingSessionView>> GetAsync(LearnerCaller caller, Guid id, CancellationToken ct);
    Task<AuthResult<StartTrainingSessionResponse>> StartAsync(LearnerCaller caller, Guid id, string? key, CancellationToken ct);
    Task<AuthResult<TrainingSessionView>> LaunchedAsync(LearnerCaller caller, Guid id, LaunchedTrainingSessionRequest request, CancellationToken ct);
    Task<AuthResult<HeartbeatResponse>> HeartbeatAsync(LearnerCaller caller, Guid id, CancellationToken ct);
    Task<AuthResult<TrainingEventsResponse>> RecordEventsAsync(LearnerCaller caller, Guid id, TrainingEventsRequest request, CancellationToken ct);
    Task<AuthResult<(int Status, CompleteTrainingSessionResponse Body)>> CompleteAsync(LearnerCaller caller, Guid id, CompleteTrainingSessionRequest request, string? key, CancellationToken ct);
    Task<AuthResult<TrainingResultView>> ResultAsync(LearnerCaller caller, Guid id, CancellationToken ct);
    Task<AuthResult<TrainingContinuation>> ContinuationAsync(LearnerCaller caller, Guid id, CancellationToken ct);
    Task<AuthResult<IReadOnlyList<TrainingSessionView>>> ReconcileAsync(LearnerCaller caller, ReconcileTrainingRequest request, CancellationToken ct);
}

public sealed record BuildingQrView(Guid Id, Guid BuildingId, string? Label, string Status, DateTime? CreatedAt = null, DateTime? RevokedAt = null, Guid? ReplacedBy = null, Guid? ReplacedId = null);
/// <summary>Token is returned only on create/rotate; the QR encodes it. It identifies a Building, nothing else.</summary>
public sealed record BuildingQrCreated(BuildingQrView Code, string Token);
public sealed record CreateBuildingQrRequest(string? Label);
public sealed record BuildingQrResolution(Guid BuildingId, string BuildingName, string Visibility, bool HasAccess);

public interface IBuildingQrCodes
{
    Task<AuthResult<BuildingQrCreated>> CreateAsync(Guid actor, Guid family, Guid building, CreateBuildingQrRequest request, CancellationToken ct);
    Task<AuthResult<IReadOnlyList<BuildingQrView>>> ListAsync(Guid actor, Guid family, Guid building, CancellationToken ct);
    Task<AuthResult<BuildingQrCreated>> RotateAsync(Guid actor, Guid family, Guid building, Guid qr, CreateBuildingQrRequest request, CancellationToken ct);
    Task<AuthResult<BuildingQrView>> RevokeAsync(Guid actor, Guid family, Guid building, Guid qr, CancellationToken ct);
    Task<AuthResult<BuildingQrResolution>> ResolveAsync(Guid actor, Guid family, string token, CancellationToken ct);
}
