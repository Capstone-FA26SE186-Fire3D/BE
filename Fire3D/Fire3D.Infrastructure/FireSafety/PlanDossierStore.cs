using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.FireSafety;
using Fire3D.Infrastructure.Persistence;

namespace Fire3D.Infrastructure.FireSafety;

/// <summary>Hồ sơ phương án of a Building through plan_dossier_gate (BE#215).</summary>
public sealed class PlanDossierStore(Fire3DDbContext db) : IPlanDossierStore
{
    public Task<AuthResult<JsonElement>> GetAsync(Guid actor, Guid? family, Guid building, Guid? infrastructureId, CancellationToken ct) =>
        JsonCommandGate.Execute(db, "plan_dossier_gate", "Get", actor, family, building, new { infrastructureId }, null, null, ct);
}
