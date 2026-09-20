using Fire3D.Application.Authentication;
using MediatR;


namespace Fire3D.Application.Ifc.Commands.ProcessRevision;

public sealed record ProcessRevisionCommand(Guid ActorId, Guid RevisionId) 
    : IRequest<AuthResult<Guid>>;

public sealed class ProcessRevisionHandler(IAuthStore accounts, IIfcWriteStore store)
    : IRequestHandler<ProcessRevisionCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(ProcessRevisionCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        return await store.ProcessRevisionAsync(command.ActorId, command.RevisionId, scope.Value.OrganizationId, ct);
    }
}
