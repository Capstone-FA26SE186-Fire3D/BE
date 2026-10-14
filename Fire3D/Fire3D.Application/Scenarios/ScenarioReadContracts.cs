using Fire3D.Application.Administration;
using System.Text.Json.Nodes;

namespace Fire3D.Application.Scenarios;

public sealed record ScenarioSummaryResponse(Guid Id, Guid BuildingId, string Name, DateTime CreatedAt);
/// <summary>CurrentDraft* is the most recently saved draft (send CurrentDraftVersion as If-Match); LatestVersion* is the newest snapshot.</summary>
public sealed record ScenarioDetailResponse(Guid Id, Guid BuildingId, Guid OrganizationId, string Name, DateTime CreatedAt,
    Guid? CurrentDraftId = null, uint? CurrentDraftVersion = null, int DraftCount = 0, Guid? LatestVersionId = null, int? LatestVersionNumber = null);
/// <summary>Draft without its state. Version is the ETag revision; LastEditedBy comes from the draft audit (creator when never saved).
/// StateKind: Uninitialized (state is still {}), Versioned (schemaVersion set, see SchemaVersion) or Legacy (typed pre-editor shape).</summary>
public sealed record ScenarioDraftSummaryResponse(Guid Id, Guid ScenarioId, Guid RevisionId, int DraftNumber, string Source, string? SchemaVersion,
    uint Version, Guid CreatedBy, DateTime CreatedAt, DateTime UpdatedAt, Guid? LastEditedById, string? LastEditedByName, string StateKind = "Legacy");
public sealed record ScenarioDraftResponse(Guid Id, Guid ScenarioId, Guid RevisionId, Guid BuildingId,
    Guid OrganizationId, int DraftNumber, JsonNode State, string Source, Guid? LastAiRequestId,
    DateTime CreatedAt, DateTime UpdatedAt, uint Version);
public sealed record ScenarioVersionSummaryResponse(Guid Id, Guid ScenarioId, Guid RevisionId, Guid BuildingId,
    Guid OrganizationId, int VersionNumber, string Name, string SchemaVersion, string AlgorithmVersion,
    int TimeLimitSeconds, string ScenarioHash, DateTime CreatedAt, string ReviewStatus="NotSubmitted", Guid? ReviewId=null, string? RejectReason=null);
public sealed record ScenarioVersionConfigurationResponse(JsonNode Spawn, JsonNode Goal, JsonNode FireSource,
    JsonNode Npc, JsonNode BlockedElements, JsonNode Routing, JsonNode Scoring, JsonNode ModePolicy,
    JsonNode SafetyThresholds);
public sealed record ScenarioVersionDetailResponse(Guid Id, Guid ScenarioId, Guid RevisionId, Guid BuildingId,
    Guid OrganizationId, int VersionNumber, string Name, string SchemaVersion, string AlgorithmVersion,
    long RandomSeed, int TimeLimitSeconds, int ReplanIntervalSeconds, string ScenarioHash,
    ScenarioVersionConfigurationResponse Configuration, DateTime CreatedAt, JsonNode? StateSnapshot = null, JsonNode? Rubric = null, JsonNode? LearningObjectives = null, string? LearnerInstructions = null,
    string ReviewStatus="NotSubmitted", Guid? ReviewId=null, string? RejectReason=null);

public interface IScenarioReadStore
{
    Task<IReadOnlyList<Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ScenarioDraftValidationIssue>> ValidateReferencesAsync(Guid revision,JsonNode state,CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ScenarioDraftValidationIssue>>([]);
    Task<PageResponse<ScenarioSummaryResponse>?> ListBuildingScenariosAsync(
        Guid buildingId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<ScenarioDetailResponse?> GetScenarioAsync(Guid scenarioId, Guid? organizationId, CancellationToken ct);
    Task<ScenarioDraftResponse?> GetScenarioDraftAsync(Guid draftId, Guid? organizationId, CancellationToken ct);
    Task<PageResponse<ScenarioDraftSummaryResponse>?> ListScenarioDraftsAsync(Guid scenarioId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<PageResponse<ScenarioVersionSummaryResponse>?> ListScenarioVersionsAsync(
        Guid scenarioId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<ScenarioVersionDetailResponse?> GetScenarioVersionAsync(
        Guid versionId, Guid? organizationId, CancellationToken ct);
}
