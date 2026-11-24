using System.Text.Json.Nodes;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;

namespace Fire3D.Application.Expert;

/// <summary>
/// One item of the FireSafetyExpert queue. Kind Scenario (BE#211): a scenario version; ReviewId is the content review, and the Scenario*
/// and RubricHash fields are set. Kind Infrastructure (BE#213): a fire infrastructure version; ReviewId = InfrastructureId and
/// VersionNumber is the infrastructure version. Revision is the ETag value for decisions (Scenario: 1 while Submitted, 2 once decided;
/// Infrastructure: the version's revision). DecidedBy names the expert who decided.
/// </summary>
public sealed record ExpertReviewItem(Guid ReviewId, string Kind, string Status, long Revision, Guid? ScenarioVersionId, Guid RevisionId,
    string? ContentHash, string? RubricHash, Guid SubmittedBy, string? SubmittedByName, DateTimeOffset SubmittedAt,
    Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt, string? Reason,
    Guid OrganizationId, string OrganizationName, Guid BuildingId, string BuildingName, Guid? ScenarioId, string? ScenarioName, int VersionNumber,
    Guid? InfrastructureId = null);
/// <summary>Frozen content under thẩm định, read-only. Scenario: state snapshot (scenario objects and its infrastructure items), rubric,
/// objectives, instructions. Infrastructure: Content = {geometry, runtimeVersion, items}; the other fields are null.</summary>
public sealed record ExpertReviewDetail(ExpertReviewItem Review, JsonNode? Content, JsonNode? Rubric, JsonNode? LearningObjectives, string? LearnerInstructions);
public sealed record ExpertRejectRequest(string? Reason);
public sealed record ExpertReviewFilter(string? Kind = null, string? Status = null, int Page = 1, int PageSize = 20);

public interface IExpertReviews
{
    Task<AuthResult<PageResponse<ExpertReviewItem>>> ListAsync(Guid actor, Guid? family, ExpertReviewFilter filter, CancellationToken ct);
    Task<AuthResult<ExpertReviewDetail>> GetAsync(Guid actor, Guid? family, Guid reviewId, CancellationToken ct);
    /// <summary>The 3D preview of the revision the reviewed scenario uses; nothing else of that organization is opened.</summary>
    Task<AuthResult<EditorPreviewResponse>> PreviewAsync(Guid actor, Guid? family, Guid reviewId, CancellationToken ct);
    /// <summary>Approve or Reject a Submitted review; reason is required for Reject (1-2000 characters).</summary>
    Task<AuthResult<ExpertReviewItem>> DecideAsync(Guid actor, Guid? family, Guid reviewId, bool approve, string? reason, long expectedRevision, string key, CancellationToken ct);
}
