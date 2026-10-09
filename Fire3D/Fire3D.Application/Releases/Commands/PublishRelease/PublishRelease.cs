using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Releases;
using MediatR;

namespace Fire3D.Application.Releases.Commands.PublishRelease;

public sealed record PublishReleaseCommand(Guid ActorId, Guid ReleaseId, Guid? FamilyId = null, string? Key = null) : IRequest<AuthResult<ReleaseResponse>>;

public sealed class PublishReleaseHandler(IAuthStore accounts, IReleaseStore store)
    : IRequestHandler<PublishReleaseCommand, AuthResult<ReleaseResponse>>
{
    public async Task<AuthResult<ReleaseResponse>> Handle(PublishReleaseCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        if (command.ReleaseId == Guid.Empty || string.IsNullOrWhiteSpace(command.Key) || command.Key.Length > 128 || command.Key.Any(char.IsControl))
            return AuthResult<ReleaseResponse>.Fail("VALIDATION_ERROR", "Release id and Idempotency-Key (1–128 characters) are required.", 400);
        return await store.PublishAsync(command.ActorId, command.ReleaseId, scope.Value!.OrganizationId, ct, command.FamilyId, command.Key);
    }
}
