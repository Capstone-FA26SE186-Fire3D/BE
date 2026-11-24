using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.FireSafety;
using Fire3D.Infrastructure.Persistence;

namespace Fire3D.Infrastructure.FireSafety;

/// <summary>Fire infrastructure versions through fire_infrastructure_gate (BE#213).</summary>
public sealed class FireInfrastructureStore(Fire3DDbContext db) : IFireInfrastructureStore
{
    public Task<AuthResult<JsonElement>> ExecuteAsync(string action, Guid actor, Guid? family, Guid resource, object input, string? key, long? expected, CancellationToken ct) =>
        JsonCommandGate.Execute(db, "fire_infrastructure_gate", action, actor, family, resource, input, key, expected, ct);
}
