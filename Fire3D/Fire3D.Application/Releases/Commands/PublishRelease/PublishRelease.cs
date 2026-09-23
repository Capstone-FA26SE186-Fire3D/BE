using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Releases;
using MediatR;

namespace Fire3D.Application.Releases.Commands.PublishRelease;

public sealed record PublishReleaseCommand(Guid ActorId, Guid ReleaseId) : IRequest<AuthResult<bool>>;

public sealed class PublishReleaseHandler(IAuthStore accounts, IReleaseStore store)
    : IRequestHandler<PublishReleaseCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(PublishReleaseCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        return await store.PublishAsync(command.ActorId, command.ReleaseId, scope.Value!.OrganizationId, ct);
    }
}
