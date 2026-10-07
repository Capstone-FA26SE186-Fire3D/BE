using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.SnapshotScenarioDraft;

public sealed record SnapshotScenarioDraftCommand(Guid ActorId, Guid DraftId, uint? ExpectedVersion = null, string? IdempotencyKey = null) : IRequest<AuthResult<Guid>>;

public sealed class SnapshotScenarioDraftHandler(IAuthStore accounts, IScenarioWriteStore store)
    : IRequestHandler<SnapshotScenarioDraftCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(SnapshotScenarioDraftCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        return await store.SnapshotScenarioDraftAsync(command.ActorId, command.DraftId, scope.Value!.OrganizationId, ct, command.IdempotencyKey, command.ExpectedVersion);
    }
}
