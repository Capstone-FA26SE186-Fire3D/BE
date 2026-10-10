using System.Text.Json.Nodes;
using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class RuntimeCatalogReadStore(Fire3DDbContext db) : IRuntimeCatalogReadStore
{
    // capability_contracts is read through SQL so the EF model and snapshot stay unchanged.
    private const string Select = "SELECT runtime_version,protocol_version,manifest_schema_version,capabilities::text,capability_contracts::text FROM runtime_compatibility_catalog WHERE is_active";

    public Task<List<RuntimeCatalogDto>> GetActiveCatalogAsync(CancellationToken ct) =>
        ReadAsync(Select + " ORDER BY runtime_version,created_at DESC,id", null, ct);

    public async Task<RuntimeCatalogDto?> GetActiveRuntimeAsync(string runtimeVersion, CancellationToken ct) =>
        (await ReadAsync(Select + " AND runtime_version=@runtime ORDER BY created_at DESC,id LIMIT 1", runtimeVersion, ct)).SingleOrDefault();

    private async Task<List<RuntimeCatalogDto>> ReadAsync(string sql, string? runtime, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
            if (runtime is not null) command.Parameters.AddWithValue("runtime", runtime);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<RuntimeCatalogDto>();
            while (await reader.ReadAsync(ct))
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), JsonNode.Parse(reader.GetString(3))!,
                    JsonNode.Parse(reader.GetString(4)) as JsonObject));
            return rows;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
