using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.StartPlaytestSession;

public sealed record StartPlaytestSessionCommand(Guid ActorId, Guid PlaytestId) : IRequest<AuthResult<bool>>;

public sealed class StartPlaytestSessionHandler(IAuthStore accounts, Fire3D.Application.Scenarios.Commands.PreparePlaytestSession.IPlaytestWriteStore store)
    : IRequestHandler<StartPlaytestSessionCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(StartPlaytestSessionCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        return await store.StartPlaytestAsync(command.ActorId, command.PlaytestId, scope.Value!.OrganizationId ?? Guid.Empty, ct);
    }
}
