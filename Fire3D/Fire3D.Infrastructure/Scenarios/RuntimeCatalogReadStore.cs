using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class RuntimeCatalogReadStore(Fire3DDbContext db) : IRuntimeCatalogReadStore
{
    public async Task<List<RuntimeCatalogDto>> GetActiveCatalogAsync(CancellationToken ct)
    {
        return await db.RuntimeCompatibilityCatalogs
            .Where(c => c.IsActive)
            .Select(c => new RuntimeCatalogDto(c.RuntimeVersion, c.ProtocolVersion, c.ManifestSchemaVersion, c.Capabilities))
            .ToListAsync(ct);
    }
}
