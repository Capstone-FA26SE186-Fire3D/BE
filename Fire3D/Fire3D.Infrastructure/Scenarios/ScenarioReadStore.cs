using Fire3D.Application.Administration;
using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class ScenarioReadStore(Fire3DDbContext db) : IScenarioReadStore
{
    public async Task<ScenarioVersionDetailResponse?> GetScenarioVersionAsync(
        Guid versionId, Guid? organizationId, CancellationToken ct)
    {
        var query = db.ScenarioVersions.AsNoTracking().Where(v => v.Id == versionId);
        if (organizationId.HasValue) query = query.Where(v => v.OrganizationId == organizationId.Value);

        var version = await query.Where(v => v.Scenario.Building.IsActive && v.Scenario.Building.DeletedAt == null)
            .SingleOrDefaultAsync(ct);
        if (version is null) return null;

        return new ScenarioVersionDetailResponse(version.Id, version.ScenarioId, version.RevisionId, version.BuildingId,
            version.OrganizationId, version.VersionNumber, version.Name, version.SchemaVersion, version.AlgorithmVersion,
            version.RandomSeed, version.TimeLimitSeconds, version.ReplanIntervalSeconds, version.ScenarioHash,
            new ScenarioVersionConfigurationResponse(
                System.Text.Json.Nodes.JsonNode.Parse(version.SpawnConfig)!,
                System.Text.Json.Nodes.JsonNode.Parse(version.GoalConfig)!,
                System.Text.Json.Nodes.JsonNode.Parse(version.FireSourceConfig)!,
                System.Text.Json.Nodes.JsonNode.Parse(version.NpcConfig)!,
                System.Text.Json.Nodes.JsonNode.Parse(version.BlockedElements)!,
                System.Text.Json.Nodes.JsonNode.Parse(version.RoutingConfig)!,
                System.Text.Json.Nodes.JsonNode.Parse(version.ScoringConfig)!,
                System.Text.Json.Nodes.JsonNode.Parse(version.ModePolicy)!,
                System.Text.Json.Nodes.JsonNode.Parse(version.SafetyThresholds)!),
            version.CreatedAt);
    }

    public async Task<PageResponse<ScenarioVersionSummaryResponse>?> ListScenarioVersionsAsync(
        Guid scenarioId, Guid? organizationId, int page, int pageSize, CancellationToken ct)
    {
        var scenarioExists = await db.Scenarios.AsNoTracking().AnyAsync(s =>
            s.Id == scenarioId && s.Building.IsActive && s.Building.DeletedAt == null
            && (!organizationId.HasValue || s.OrganizationId == organizationId.Value), ct);
        if (!scenarioExists) return null;

        var query = db.ScenarioVersions.AsNoTracking().Where(v => v.ScenarioId == scenarioId);
        if (organizationId.HasValue) query = query.Where(v => v.OrganizationId == organizationId.Value);

        var totalCount = await query.CountAsync(ct);
        var items = await query.OrderByDescending(v => v.VersionNumber).ThenByDescending(v => v.CreatedAt).ThenBy(v => v.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(v => new ScenarioVersionSummaryResponse(v.Id, v.ScenarioId, v.RevisionId, v.BuildingId,
                v.OrganizationId, v.VersionNumber, v.Name, v.SchemaVersion, v.AlgorithmVersion,
                v.TimeLimitSeconds, v.ScenarioHash, v.CreatedAt))
            .ToListAsync(ct);
        return new PageResponse<ScenarioVersionSummaryResponse>(items, totalCount, page, pageSize);
    }

    public Task<ScenarioDraftResponse?> GetScenarioDraftAsync(Guid draftId, Guid? organizationId, CancellationToken ct)
    {
        var query = db.ScenarioDrafts.AsNoTracking().Where(d => d.Id == draftId);
        if (organizationId.HasValue) query = query.Where(d => d.OrganizationId == organizationId.Value);
        return query.Where(d => d.Building.IsActive && d.Building.DeletedAt == null)
            .Select(d => new ScenarioDraftResponse(d.Id, d.ScenarioId, d.RevisionId, d.BuildingId,
                d.OrganizationId, d.DraftNumber, d.State, d.Source, d.LastAiRequestId,
                d.CreatedAt, d.UpdatedAt, d.Version))
            .SingleOrDefaultAsync(ct);
    }

    public Task<ScenarioDetailResponse?> GetScenarioAsync(Guid scenarioId, Guid? organizationId, CancellationToken ct)
    {
        var query = db.Scenarios.AsNoTracking().Where(s => s.Id == scenarioId);
        if (organizationId.HasValue) query = query.Where(s => s.OrganizationId == organizationId.Value);

        return query.Where(s => s.Building.IsActive && s.Building.DeletedAt == null)
            .Select(s => new ScenarioDetailResponse(s.Id, s.BuildingId, s.OrganizationId, s.Name, s.CreatedAt))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<PageResponse<ScenarioSummaryResponse>?> ListBuildingScenariosAsync(
        Guid buildingId, Guid? organizationId, int page, int pageSize, CancellationToken ct)
    {
        var buildingExists = await db.Buildings.AsNoTracking().AnyAsync(b =>
            b.Id == buildingId && b.IsActive && b.DeletedAt == null
            && (!organizationId.HasValue || b.OrganizationId == organizationId.Value), ct);
        if (!buildingExists) return null;

        var query = db.Scenarios.AsNoTracking().Where(s => s.BuildingId == buildingId);
        if (organizationId.HasValue) query = query.Where(s => s.OrganizationId == organizationId.Value);

        var totalCount = await query.CountAsync(ct);
        var items = await query.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new ScenarioSummaryResponse(s.Id, s.BuildingId, s.Name, s.CreatedAt))
            .ToListAsync(ct);
        return new PageResponse<ScenarioSummaryResponse>(items, totalCount, page, pageSize);
    }
}
