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

public interface IScenarioReviewQueries
{
    Task<AuthResult<PageResponse<ScenarioReviewSummary>>> ListAsync(Guid actor, Guid? family, string? status,
        Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<AuthResult<ScenarioReviewDetail>> DetailAsync(Guid actor, Guid? family, Guid id, bool byVersion, CancellationToken ct);
    Task<AuthResult<IReadOnlyList<ScenarioVersionReviewState>>> StatesAsync(Guid actor, Guid? family, IReadOnlyList<Guid> ids, CancellationToken ct);
}
