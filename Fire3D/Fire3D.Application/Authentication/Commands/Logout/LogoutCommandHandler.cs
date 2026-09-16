using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.Logout;

internal sealed class LogoutCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<LogoutCommand>
{
    public async Task Handle(LogoutCommand command, CancellationToken ct)
    {
        var userId = command.UserId;
        var familyId = command.FamilyId;
        await using var transaction = await store.BeginUserTransactionAsync(userId, ct);
        var now = AuthSupport.UtcNow(clock);
        await store.RevokeFamilyAsync(userId, familyId, now, ct);
        var user = await store.FindUserAsync(userId, ct);
        if (user is not null) await store.WriteAuditAsync(user, "Logout", user.Id, now, ct);
        await transaction.CommitAsync(ct);
    }
}
