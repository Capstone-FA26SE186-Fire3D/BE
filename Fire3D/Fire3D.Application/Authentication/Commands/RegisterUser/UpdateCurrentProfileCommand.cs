using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

public sealed record UpdateCurrentProfileRequest(string FullName);
public sealed record UpdateCurrentProfileCommand(Guid UserId, UpdateCurrentProfileRequest Request)
    : IRequest<AuthResult<AccountResponse>>;

public sealed class UpdateCurrentProfileCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<UpdateCurrentProfileCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(UpdateCurrentProfileCommand command, CancellationToken ct)
    {
        var name = command.Request?.FullName?.Trim();
        if (command.UserId == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 200)
            return AuthResult<AccountResponse>.Fail("VALIDATION_ERROR", "Full name must contain 1–200 characters.", 400);

        await using var transaction = await store.BeginUserTransactionAsync(command.UserId, ct);
        var user = await store.FindUserAsync(command.UserId, ct);
        if (user is null || !await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<AccountResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);

        var now = AuthSupport.UtcNow(clock);
        user.FullName = name;
        user.UpdatedAt = now;
        await store.UpdateUserAsync(user, ct);
        await store.WriteAuditAsync(user, "Update", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<AccountResponse>.Ok(AuthSupport.ToAccount(user));
    }
}
