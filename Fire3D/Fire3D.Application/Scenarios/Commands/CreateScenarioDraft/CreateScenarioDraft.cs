using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;

/// <summary>BE#213: InfrastructureId is the Approved fire infrastructure version of the same revision the scenario builds on (required).</summary>
public sealed record CreateScenarioDraftRequest(Guid RevisionId, Guid? InfrastructureId = null);

public sealed record CreateScenarioDraftCommand(Guid ActorId, Guid ScenarioId, CreateScenarioDraftRequest Request, string? IdempotencyKey = null) : IRequest<AuthResult<Guid>>;

public sealed class CreateScenarioDraftHandler(IAuthStore accounts, IScenarioWriteStore store)
    : IRequestHandler<CreateScenarioDraftCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(CreateScenarioDraftCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        if (command.Request == null || command.Request.RevisionId == Guid.Empty)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "RevisionId is required.", 400);
        // FR-PLAN-06: a scenario is created only on a locked (Approved) fire infrastructure; the gate checks status and revision.
        if (command.Request.InfrastructureId is not Guid infrastructure || infrastructure == Guid.Empty)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "infrastructureId is required.", 400,
                new Dictionary<string, string[]> { ["infrastructureId"] = ["Choose an Approved fire infrastructure version of this revision."] });

        return await store.CreateScenarioDraftAsync(command.ActorId, command.ScenarioId, scope.Value!.OrganizationId, command.Request, ct, command.IdempotencyKey);
    }
}
