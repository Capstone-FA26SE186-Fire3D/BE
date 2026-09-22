using Fire3D.Application.Administration;
using System.Text.Json.Nodes;

namespace Fire3D.Application.Scenarios;

public sealed record ScenarioSummaryResponse(Guid Id, Guid BuildingId, string Name, DateTime CreatedAt);
public sealed record ScenarioDetailResponse(Guid Id, Guid BuildingId, Guid OrganizationId, string Name, DateTime CreatedAt);
public sealed record ScenarioDraftResponse(Guid Id, Guid ScenarioId, Guid RevisionId, Guid BuildingId,
    Guid OrganizationId, int DraftNumber, JsonNode State, string Source, Guid? LastAiRequestId,
    DateTime CreatedAt, DateTime UpdatedAt, uint Version);

public interface IScenarioReadStore
{
    Task<PageResponse<ScenarioSummaryResponse>?> ListBuildingScenariosAsync(
        Guid buildingId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<ScenarioDetailResponse?> GetScenarioAsync(Guid scenarioId, Guid? organizationId, CancellationToken ct);
    Task<ScenarioDraftResponse?> GetScenarioDraftAsync(Guid draftId, Guid? organizationId, CancellationToken ct);
}
