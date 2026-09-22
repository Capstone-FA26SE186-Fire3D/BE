using Fire3D.Application.Administration;
using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class ScenarioReadStore(Fire3DDbContext db) : IScenarioReadStore
{
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
