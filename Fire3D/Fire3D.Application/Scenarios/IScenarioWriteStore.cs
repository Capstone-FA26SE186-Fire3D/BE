using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.CreateScenario;
using Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;

namespace Fire3D.Application.Scenarios;

public interface IScenarioWriteStore
{
    Task<AuthResult<Guid>> CreateScenarioAsync(Guid actorId, Guid buildingId, Guid organizationId, CreateScenarioRequest request, CancellationToken ct);
    Task<AuthResult<Guid>> CreateScenarioDraftAsync(Guid actorId, Guid scenarioId, Guid organizationId, CreateScenarioDraftRequest request, CancellationToken ct);
    Task<AuthResult<uint>> UpdateScenarioDraftAsync(Guid actorId, Guid draftId, uint expectedVersion, Fire3D.Application.Scenarios.Dto.ScenarioDraftStateDto state, Guid organizationId, CancellationToken ct);
    Task<AuthResult<Guid>> SnapshotScenarioDraftAsync(Guid actorId, Guid draftId, Guid organizationId, CancellationToken ct);
}
