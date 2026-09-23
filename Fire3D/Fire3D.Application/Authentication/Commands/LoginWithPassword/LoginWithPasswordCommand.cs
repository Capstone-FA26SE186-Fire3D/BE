using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.LoginWithPassword;

public sealed record LoginWithPasswordCommand(string Email, string Password) : IRequest<AuthResult<LoginResponse>>;

public sealed class LoginWithPasswordCommandHandler(IAuthStore store, IPasswordService passwords,
    ITokenService tokens, TimeProvider clock) : IRequestHandler<LoginWithPasswordCommand, AuthResult<LoginResponse>>
{
    public async Task<AuthResult<LoginResponse>> Handle(LoginWithPasswordCommand command, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(command.Email);

        if (email is null || string.IsNullOrEmpty(command.Password) || command.Password.Length > 128)
            return AuthResult<LoginResponse>.Fail("VALIDATION_ERROR", "Email and password are required (password maximum 128 characters).", 400);
        var user = await store.FindUserByEmailAsync(email, ct);

        if (user is null)
        {
            passwords.VerifyDummy(command.Password);
            return InvalidCredentials();
        }
        // Re-read and verify under the same lock as password reset, refresh and disable.
        await using var tx = await store.BeginUserTransactionAsync(user.Id, ct);
        user = await store.FindUserAsync(user.Id, ct);
        if (user is null) { passwords.VerifyDummy(command.Password); return InvalidCredentials(); }
        if (!passwords.Verify(user, command.Password, out var rehash)) return InvalidCredentials();
        if (!await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<LoginResponse>.Fail("ACCOUNT_DISABLED", "Account or organization is unavailable.", 403);
        if (rehash)
        {
            user.PasswordHash = passwords.Hash(user, command.Password);
            await store.UpdateUserAsync(user, ct);
        }
        var now = AuthSupport.UtcNow(clock);
        await store.UpdateLoginAsync(user.Id, now, ct);
        var response = await AuthSupport.IssueAsync(store, tokens, user, Guid.NewGuid(), now, now.Add(tokens.RefreshTokenLifetime), ct);
        await store.WriteAuditAsync(user, "Login", user.Id, now, ct);
        await tx.CommitAsync(ct);
        return AuthResult<LoginResponse>.Ok(new(response.AccessToken, response.RefreshToken, response.User));
    }
    private static AuthResult<LoginResponse> InvalidCredentials() =>
        AuthResult<LoginResponse>.Fail("INVALID_CREDENTIALS", "Invalid email or password.", 401);
}
