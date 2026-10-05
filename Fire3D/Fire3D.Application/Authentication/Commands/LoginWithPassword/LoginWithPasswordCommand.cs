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
        // PBKDF2 and an optional upgrade happen without holding a database lock.
        var expectedHash=user.PasswordHash;
        var expectedEmail=user.Email;
        bool verified; bool rehash;
        string? upgradedHash;
        using(AuthDiagnostics.Measure(LoginPhase.PasswordVerification))
        {
            verified=passwords.Verify(user,command.Password,out rehash);
            upgradedHash=verified && rehash ? passwords.Hash(user,command.Password) : null;
        }
        if(!verified) return InvalidCredentials();
        // Reset, password change and disable serialize on this same lifecycle/user lock.
        IAuthTransaction transaction;
        using(AuthDiagnostics.Measure(LoginPhase.LockWait)) transaction=await store.BeginUserTransactionAsync(user.Id,ct);
        await using var tx=transaction;
        using(AuthDiagnostics.Measure(LoginPhase.Revalidation)) user=await store.FindUserAsync(user.Id,ct);
        if (user is null || !string.Equals(user.Email,expectedEmail,StringComparison.Ordinal)
            || PasswordResetValidation.NormalizeEmail(user.Email) != email
            || !string.Equals(user.PasswordHash,expectedHash,StringComparison.Ordinal)) return InvalidCredentials();
        if (!await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<LoginResponse>.Fail("ACCOUNT_DISABLED", "Account or organization is unavailable.", 403);
        if (AuthSupport.RegistrationHasExpired(user, AuthSupport.UtcNow(clock)))
            return AuthResult<LoginResponse>.Fail("REGISTRATION_EXPIRED", "This unverified registration has expired. Register again to receive a new link.", 403);
        if (AuthSupport.IsPendingEmailVerification(user))
            return AuthResult<LoginResponse>.Fail("EMAIL_NOT_VERIFIED", "Verify your email before signing in.", 403);
        var now = AuthSupport.UtcNow(clock);
        using var persistence=AuthDiagnostics.Measure(LoginPhase.Persistence);
        var family=Guid.NewGuid();var raw=tokens.CreateRefreshToken();
        await store.FinalizePasswordLoginAsync(user,upgradedHash,new Fire3D.Domain.Entities.RefreshToken
        {
            Id=Guid.NewGuid(),UserId=user.Id,FamilyId=family,TokenHash=tokens.HashRefreshToken(raw),
            CreatedAt=now,ExpiresAt=now.Add(tokens.RefreshTokenLifetime)
        },now,ct);
        var access=tokens.CreateAccessToken(user,family,now);
        await tx.CommitAsync(ct);
        user.LastLoginAt=now;user.UpdatedAt=now;
        return AuthResult<LoginResponse>.Ok(new(access.Value,raw,AuthSupport.ToAccount(user)));
    }
    private static AuthResult<LoginResponse> InvalidCredentials() =>
        AuthResult<LoginResponse>.Fail("INVALID_CREDENTIALS", "Invalid email or password.", 401);
}
