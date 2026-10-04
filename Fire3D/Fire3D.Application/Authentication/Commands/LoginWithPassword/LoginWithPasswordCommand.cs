using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.LoginWithPassword;

public sealed record LoginWithPasswordCommand(string Email, string Password) : IRequest<AuthResult<LoginResponse>>;

public sealed class LoginWithPasswordCommandHandler(IAuthStore store, IPasswordService passwords,
    ITokenService tokens, TimeProvider clock) : IRequestHandler<LoginWithPasswordCommand, AuthResult<LoginResponse>>
{
    public async Task<AuthResult<LoginResponse>> Handle(LoginWithPasswordCommand command, CancellationToken ct)
    {
        using var total=AuthDiagnostics.Measure(LoginPhase.Total);
        var email = PasswordResetValidation.NormalizeEmail(command.Email);

        if (email is null || string.IsNullOrEmpty(command.Password) || command.Password.Length > 128)
            return AuthResult<LoginResponse>.Fail("VALIDATION_ERROR", "Email and password are required (password maximum 128 characters).", 400);
        Fire3D.Domain.Entities.User? user;
        using(AuthDiagnostics.Measure(LoginPhase.Lookup)) user=await store.FindUserByEmailAsync(email,ct);

        if (user is null)
        {
            passwords.VerifyDummy(command.Password);
            return InvalidCredentials();
        }
        // Re-read and verify under the same lock as password reset, refresh and disable.
        IAuthTransaction transaction;
        using(AuthDiagnostics.Measure(LoginPhase.LockWait)) transaction=await store.BeginUserTransactionAsync(user.Id,ct);
        await using var tx=transaction;
        using(AuthDiagnostics.Measure(LoginPhase.Revalidation)) user=await store.FindUserAsync(user.Id,ct);
        if (user is null) { passwords.VerifyDummy(command.Password); return InvalidCredentials(); }
        bool verified; bool rehash;
        using(AuthDiagnostics.Measure(LoginPhase.PasswordVerification)) verified=passwords.Verify(user,command.Password,out rehash);
        if (!verified) return InvalidCredentials();
        if (!await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<LoginResponse>.Fail("ACCOUNT_DISABLED", "Account or organization is unavailable.", 403);
        if (AuthSupport.RegistrationHasExpired(user, AuthSupport.UtcNow(clock)))
            return AuthResult<LoginResponse>.Fail("REGISTRATION_EXPIRED", "This unverified registration has expired. Register again to receive a new link.", 403);
        if (AuthSupport.IsPendingEmailVerification(user))
            return AuthResult<LoginResponse>.Fail("EMAIL_NOT_VERIFIED", "Verify your email before signing in.", 403);
        if (rehash)
        {
            user.PasswordHash = passwords.Hash(user, command.Password);
            await store.UpdatePasswordHashAsync(user.Id, user.PasswordHash, AuthSupport.UtcNow(clock), ct);
        }
        var now = AuthSupport.UtcNow(clock);
        using var persistence=AuthDiagnostics.Measure(LoginPhase.Persistence);
        await store.UpdateLoginAsync(user.Id, now, ct);
        var response = await AuthSupport.IssueAsync(store, tokens, user, Guid.NewGuid(), now, now.Add(tokens.RefreshTokenLifetime), ct);
        await store.WriteAuditAsync(user, "Login", user.Id, now, ct);
        await tx.CommitAsync(ct);
        return AuthResult<LoginResponse>.Ok(new(response.AccessToken, response.RefreshToken, response.User));
    }
    private static AuthResult<LoginResponse> InvalidCredentials() =>
        AuthResult<LoginResponse>.Fail("INVALID_CREDENTIALS", "Invalid email or password.", 401);
}
