using Fire3D.Application.Authentication;
using MediatR;


namespace Fire3D.Application.Ifc.Commands.ProcessRevision;

public sealed record ProcessRevisionCommand(Guid ActorId, Guid RevisionId, string? IdempotencyKey = null)
    : IRequest<AuthResult<Guid>>;

public sealed class ProcessRevisionHandler(IAuthStore accounts, IRevisionProcessingStore store)
    : IRequestHandler<ProcessRevisionCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(ProcessRevisionCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        if (command.IdempotencyKey is not { Length: > 0 and <= 128 } || command.IdempotencyKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return AuthResult<Guid>.Fail("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key must contain 1-128 printable non-whitespace characters.", 400);
        return await store.RequestAsync(command.ActorId, command.RevisionId, command.IdempotencyKey, ct);
    }
}
