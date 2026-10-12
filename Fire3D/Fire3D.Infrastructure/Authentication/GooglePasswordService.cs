using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Authentication;

/// <summary>Adds a first local password to a Google-only account after a fresh Google sign-in.</summary>
public sealed class GooglePasswordService(Fire3DDbContext db, IAuthStore accounts, IPasswordService passwords,
    IIdentityProvider provider, TimeProvider clock) : IGooglePasswordService
{
    // A stolen BE session alone must not be enough to add a password, so the Google sign-in itself has to be recent.
    internal static readonly TimeSpan RecentSignIn = TimeSpan.FromMinutes(5);

    public async Task<AuthResult<bool>> SetAsync(Guid actorId, Guid sessionFamilyId, SetPasswordRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length > 16384)
            errors["idToken"] = ["A Google Firebase ID token from a fresh sign-in is required (maximum 16384 characters)."];
        if (!PasswordResetValidation.ValidPassword(request.NewPassword))
            errors["newPassword"] = ["Use a password of 6-128 characters that is not only whitespace."];
        if (errors.Count != 0) return AuthResult<bool>.Fail("VALIDATION_ERROR", "Invalid set-password request.", 400, errors);
        var snapshot = await accounts.FindUserAsync(actorId, ct);
        if (snapshot is null || !await GoogleAuthRules.IsActiveAsync(accounts, snapshot, ct)
            || !await accounts.FamilyIsActiveAsync(actorId, sessionFamilyId, GoogleAuthRules.UtcNow(clock), ct)) return Unavailable();
        if (!string.IsNullOrWhiteSpace(snapshot.PasswordHash)) return AlreadySet();
        if (snapshot.FirebaseUid is null)
            return AuthResult<bool>.Fail("GOOGLE_LINK_REQUIRED", "Only an account signed in with Google can set a first password.", 400);

        VerifiedIdentity identity;
        try { identity = await provider.VerifyGoogleTokenAsync(request.IdToken, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (GoogleIdentityException ex) when (ex.Failure == GoogleIdentityFailure.InvalidToken) { return InvalidToken(); }
        catch (Exception)
        { return AuthResult<bool>.Fail("GOOGLE_PROVIDER_UNAVAILABLE", "Google verification is temporarily unavailable. Try again.", 503); }
        if (identity.Uid != snapshot.FirebaseUid) return InvalidToken();
        if (identity.AuthTime is not { } signedInAt || clock.GetUtcNow() - signedInAt > RecentSignIn)
            return AuthResult<bool>.Fail("RECENT_LOGIN_REQUIRED", "Sign in with Google again, then set the password within 5 minutes.", 401);

        // No provider/password hash work while holding the database transaction.
        var passwordHash = passwords.Hash(snapshot, request.NewPassword);
        await using var tx = await GoogleIdentityTransactions.BeginAsync(db, identity.Uid, actorId, ct);
        var user = await accounts.FindUserAsync(actorId, ct);
        var now = GoogleAuthRules.UtcNow(clock);
        if (user is null || !await GoogleAuthRules.IsActiveAsync(accounts, user, ct)
            || !await accounts.FamilyIsActiveAsync(actorId, sessionFamilyId, now, ct)) return Unavailable();
        if (!string.IsNullOrWhiteSpace(user.PasswordHash)) return AlreadySet();
        if (user.FirebaseUid != identity.Uid)
            return AuthResult<bool>.Fail("ACCOUNT_CHANGED", "The linked Google identity changed. Sign in again.", 409);
        if (await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM public.password_reset_operations WHERE user_id={actorId} AND status='Pending') AS "Value"
            """).SingleAsync(ct))
            return AuthResult<bool>.Fail("PASSWORD_RESET_PENDING", "Wait for password recovery to finish before setting a password.", 409);
        await db.Users.Where(x => x.Id == actorId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.PasswordHash, passwordHash).SetProperty(x => x.UpdatedAt, now), ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.local_password_reset_tokens SET used_at={now} WHERE user_id={actorId} AND used_at IS NULL", ct);
        await accounts.InvalidateUserResetTokensAsync(actorId, ct);
        await accounts.RevokeAllUserSessionsAsync(actorId, now, ct);
        await accounts.WriteAuditAsync(user, "Update", actorId, now, ct);
        await tx.CommitAsync(ct);
        return AuthResult<bool>.Ok(true);
    }

    private static AuthResult<bool> AlreadySet() =>
        AuthResult<bool>.Fail("PASSWORD_ALREADY_SET", "This account already has a password. Use change-password.", 409);
    private static AuthResult<bool> InvalidToken() =>
        AuthResult<bool>.Fail("INVALID_FIREBASE_TOKEN", "A Google sign-in of the linked account is required.", 401);
    private static AuthResult<bool> Unavailable() =>
        AuthResult<bool>.Fail("UNAUTHORIZED", "Account, organization or session is unavailable.", 401);
}
