using System.Text.Json.Nodes;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;

namespace Fire3D.Application.Scenarios;

public sealed record ScenarioReviewSummary(Guid ReviewId, Guid ScenarioVersionId, string Status, string ContentHash,
    string RubricHash, Guid SubmittedBy, DateTimeOffset SubmittedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    string? Reason, Guid OrganizationId, string OrganizationName, Guid BuildingId, string BuildingName,
    Guid ScenarioId, string ScenarioName, int VersionNumber);
public sealed record ScenarioReviewReadiness(Guid? ValidationRunId, string? Outcome, long BlockerCount,
    Guid? ConfirmationReviewId, Guid? CandidateArtifactId, bool TechnicalReady);
public sealed record ScenarioReviewDetail(ScenarioReviewSummary Review, JsonNode? Content, JsonNode? Rubric,
    JsonNode? LearningObjectives, string? LearnerInstructions, ScenarioReviewReadiness Readiness);
public sealed record ScenarioVersionReviewState(Guid ScenarioVersionId, string ReviewStatus, Guid? ReviewId, string? RejectReason);

public sealed record ReadinessJob(Guid JobId, string Status, Guid? CurrentAttemptId);
public sealed record ReadinessBlocker(string IssueCode, string Severity, string Message);
/// <summary>Technical readiness of one scenario version, readable before submission. Missing lists what still blocks confirm/release:
/// PACKAGE_VALIDATION_REQUIRED, QA_NOT_PASSED, QA_BLOCKERS_PRESENT, CONFIRMATION_REQUIRED, RUNTIME_ARTIFACT_REQUIRED.</summary>
public sealed record ScenarioVersionReadiness(Guid ScenarioVersionId, Guid RevisionId, string ScenarioHash, string ReviewStatus,
    ReadinessJob? PackageJob, Guid? ValidationRunId, string? Outcome, long BlockerCount, IReadOnlyList<ReadinessBlocker> Blockers,
    Guid? ConfirmationReviewId, Guid? CandidateArtifactId, bool TechnicalReady, IReadOnlyList<string> Missing);
public sealed record RevisionConfirmation(Guid ReviewId, Guid ScenarioVersionId, Guid ValidationRunId, DateTimeOffset ReviewedAt);
/// <summary>Geometry processing/QA state of one revision and its technical confirmations. Missing: PROCESSING_REQUIRED,
/// GEOMETRY_VALIDATION_REQUIRED, QA_NOT_PASSED, QA_BLOCKERS_PRESENT.</summary>
public sealed record RevisionReadiness(Guid RevisionId, Guid BuildingId, string RevisionStatus, ReadinessJob? GeometryJob,
    Guid? ValidationRunId, string? Outcome, long BlockerCount, IReadOnlyDictionary<string, long> IssueCounts, bool ReadyForScenario,
    IReadOnlyList<string> Missing, IReadOnlyList<RevisionConfirmation> Confirmations);

public interface IScenarioReviewQueries
{
    Task<AuthResult<PageResponse<ScenarioReviewSummary>>> ListAsync(Guid actor, Guid? family, string? status,
        Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<AuthResult<ScenarioReviewDetail>> DetailAsync(Guid actor, Guid? family, Guid id, bool byVersion, CancellationToken ct);
    Task<AuthResult<IReadOnlyList<ScenarioVersionReviewState>>> StatesAsync(Guid actor, Guid? family, IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<AuthResult<ScenarioVersionReadiness>> VersionReadinessAsync(Guid actor, Guid? family, Guid versionId, CancellationToken ct);
    Task<AuthResult<RevisionReadiness>> RevisionReadinessAsync(Guid actor, Guid? family, Guid revisionId, CancellationToken ct);
}
