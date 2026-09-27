using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

public sealed record UpdateCurrentProfileRequest(string? FullName, string? Username = null);
public sealed record UpdateCurrentProfileCommand(Guid UserId, string? IfMatch, UpdateCurrentProfileRequest Request)
    : IRequest<AuthResult<AccountResponse>>;

public sealed class UpdateCurrentProfileCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<UpdateCurrentProfileCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(UpdateCurrentProfileCommand command, CancellationToken ct)
    {
        if (!ProfileEtag.TryParse(command.IfMatch, out var expectedRevision))
            return AuthResult<AccountResponse>.Fail(string.IsNullOrWhiteSpace(command.IfMatch) ? "PRECONDITION_REQUIRED" : "VALIDATION_ERROR",
                string.IsNullOrWhiteSpace(command.IfMatch) ? "Send the ETag from GET /api/auth/me in If-Match." : "If-Match must contain one quoted positive revision.",
                string.IsNullOrWhiteSpace(command.IfMatch) ? 428 : 400);

        var name = command.Request?.FullName?.Trim();
        var username = command.Request?.Username is null ? null : SelfRegistrationValidation.NormalizeUsername(command.Request.Username);
        if (command.UserId == Guid.Empty || command.Request is null || (name is null && command.Request.Username is null)
            || (name is not null && (name.Length == 0 || name.Length > 200))
            || (command.Request.Username is not null && username is null))
            return AuthResult<AccountResponse>.Fail("VALIDATION_ERROR", "Provide a full name up to 200 characters and/or a valid username.", 400);

        await using var transaction = await store.BeginUserTransactionAsync(command.UserId, ct);
        var user = await store.FindUserAsync(command.UserId, ct);
        if (user is null || !await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<AccountResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);

        var now = AuthSupport.UtcNow(clock);
        var result = await store.UpdateProfileAsync(user.Id, expectedRevision, name ?? user.FullName, username ?? user.Username, now, ct);
        if (result == ProfileUpdateResult.PreconditionFailed)
            return AuthResult<AccountResponse>.Fail("PRECONDITION_FAILED", "The profile changed. Reload it and retry.", 412);
        if (result == ProfileUpdateResult.UsernameTaken)
            return AuthResult<AccountResponse>.Fail("USERNAME_EXISTS", "Username is already registered.", 409);

        user.FullName = name ?? user.FullName;
        user.Username = username ?? user.Username;
        user.ProfileRevision = expectedRevision + 1;
        user.UpdatedAt = now;
        await store.WriteAuditAsync(user, "Update", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<AccountResponse>.Ok(AuthSupport.ToAccount(user));
    }
}
