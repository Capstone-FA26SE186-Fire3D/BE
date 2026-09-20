using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios.Dto;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.UpdateScenarioDraft;

public sealed record UpdateScenarioDraftCommand(Guid ActorId, Guid DraftId, uint ExpectedVersion, ScenarioDraftStateDto State) : IRequest<AuthResult<uint>>;

public sealed class UpdateScenarioDraftHandler(IAuthStore accounts, IScenarioWriteStore store)
    : IRequestHandler<UpdateScenarioDraftCommand, AuthResult<uint>>
{
    public async Task<AuthResult<uint>> Handle(UpdateScenarioDraftCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        if (command.State == null)
            return AuthResult<uint>.Fail("VALIDATION_ERROR", "Draft state is required.", 400);

        return await store.UpdateScenarioDraftAsync(command.ActorId, command.DraftId, command.ExpectedVersion, command.State, scope.Value!.OrganizationId ?? Guid.Empty, ct);
    }
}
