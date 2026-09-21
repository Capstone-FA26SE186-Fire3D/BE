using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Ifc.Commands.ConfirmForTraining;

public sealed record ConfirmForTrainingCommand(Guid ActorId, Guid RevisionId) : IRequest<AuthResult<bool>>;

public sealed class ConfirmForTrainingHandler(IAuthStore accounts, IIfcWriteStore store)
    : IRequestHandler<ConfirmForTrainingCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(ConfirmForTrainingCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        return await store.ConfirmForTrainingAsync(command.ActorId, command.RevisionId, scope.Value!.OrganizationId, ct);
    }
}
