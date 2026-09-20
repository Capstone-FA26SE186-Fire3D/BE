using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;

public sealed record CreateScenarioDraftRequest(Guid RevisionId);

public sealed record CreateScenarioDraftCommand(Guid ActorId, Guid ScenarioId, CreateScenarioDraftRequest Request) : IRequest<AuthResult<Guid>>;

public sealed class CreateScenarioDraftHandler(IAuthStore accounts, IScenarioWriteStore store)
    : IRequestHandler<CreateScenarioDraftCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(CreateScenarioDraftCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        if (command.Request == null || command.Request.RevisionId == Guid.Empty)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "RevisionId is required.", 400);

        return await store.CreateScenarioDraftAsync(command.ActorId, command.ScenarioId, scope.Value.OrganizationId ?? Guid.Empty, command.Request, ct);
    }
}
