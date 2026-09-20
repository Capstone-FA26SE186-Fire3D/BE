using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Releases.Commands.PublishRelease;

public sealed record PublishReleaseCommand(Guid ActorId, Guid ReleaseId) : IRequest<AuthResult<bool>>;

public interface IReleaseWriteStore
{
    Task<AuthResult<bool>> PublishReleaseAsync(Guid actorId, Guid releaseId, Guid organizationId, CancellationToken ct);
}

public sealed class PublishReleaseHandler(IAuthStore accounts, IReleaseWriteStore store)
    : IRequestHandler<PublishReleaseCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(PublishReleaseCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        return await store.PublishReleaseAsync(command.ActorId, command.ReleaseId, scope.Value!.OrganizationId ?? Guid.Empty, ct);
    }
}
