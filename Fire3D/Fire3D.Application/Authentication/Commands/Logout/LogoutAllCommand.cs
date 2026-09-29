using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.Logout;

public sealed record LogoutAllCommand(Guid UserId) : IRequest<AuthResult<bool>>;

public sealed class LogoutAllCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<LogoutAllCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(LogoutAllCommand command, CancellationToken ct)
    {
        if (command.UserId == Guid.Empty)
            return AuthResult<bool>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);

        await using var transaction = await store.BeginUserTransactionAsync(command.UserId, ct);
        var user = await store.FindUserAsync(command.UserId, ct);
        if (user is null || !await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<bool>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);

        var now = AuthSupport.UtcNow(clock);
        await store.RevokeAllUserSessionsAsync(user.Id, now, ct);
        await store.DisableUserPushDevicesAsync(user.Id, now, ct);
        await store.WriteAuditAsync(user, "Logout", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<bool>.Ok(true);
    }
}
