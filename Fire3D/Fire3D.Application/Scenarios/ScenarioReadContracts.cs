using Fire3D.Application.Administration;

namespace Fire3D.Application.Scenarios;

public sealed record ScenarioSummaryResponse(Guid Id, Guid BuildingId, string Name, DateTime CreatedAt);

public interface IScenarioReadStore
{
    Task<PageResponse<ScenarioSummaryResponse>?> ListBuildingScenariosAsync(
        Guid buildingId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
}
