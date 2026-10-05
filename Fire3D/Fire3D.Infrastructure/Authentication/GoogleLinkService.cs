using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fire3D.Infrastructure.Authentication;

public sealed class GoogleLinkService(Fire3DDbContext db, IAuthStore accounts, IPasswordService passwords,
    IIdentityProvider provider, TimeProvider clock) : IGoogleLinkService
{
    public async Task<AuthResult<GoogleLinkResponse>> LinkAsync(Guid actorId, Guid sessionFamilyId, GoogleLinkRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length > 16384)
            errors["idToken"] = ["A Google Firebase ID token is required (maximum 16384 characters)."];
        // This is existing-password proof, not a new-password strength requirement.
        if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > 128)
            errors["currentPassword"] = ["Current password is required (maximum 128 characters)."];
        if (errors.Count != 0) return AuthResult<GoogleLinkResponse>.Fail("VALIDATION_ERROR", "Invalid Google link request.", 400, errors);
        var snapshot = await accounts.FindUserAsync(actorId, ct);
        if (snapshot is null || !await GoogleAuthRules.IsActiveAsync(accounts, snapshot, ct)) return Unavailable();
        if (string.IsNullOrWhiteSpace(snapshot.PasswordHash))
            return AuthResult<GoogleLinkResponse>.Fail("LOCAL_PASSWORD_REQUIRED", "Only an account with a local password can link Google.", 400);
        if (!passwords.Verify(snapshot, request.CurrentPassword, out _))
            return AuthResult<GoogleLinkResponse>.Fail("INVALID_CURRENT_PASSWORD", "Current password is incorrect.", 401);

        VerifiedIdentity identity;
        try { identity = await provider.VerifyGoogleTokenAsync(request.IdToken, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (GoogleIdentityException ex) when (ex.Failure == GoogleIdentityFailure.InvalidToken)
        { return AuthResult<GoogleLinkResponse>.Fail("INVALID_FIREBASE_TOKEN", "Invalid Firebase ID token.", 401); }
        catch (Exception)
        { return AuthResult<GoogleLinkResponse>.Fail("GOOGLE_PROVIDER_UNAVAILABLE", "Google verification is temporarily unavailable. Try again.", 503); }
        if (string.IsNullOrWhiteSpace(identity.Uid) || identity.Uid.Length > 128 || PasswordResetValidation.NormalizeEmail(identity.Email) is null)
            return AuthResult<GoogleLinkResponse>.Fail("INVALID_FIREBASE_TOKEN", "Verified Google identity is required.", 401);

        // No provider/password hash work while holding the database transaction.
        await using var tx = await GoogleIdentityTransactions.BeginAsync(db, identity.Uid, actorId, ct);
        var user = await accounts.FindUserAsync(actorId, ct);
        var now = GoogleAuthRules.UtcNow(clock);
        if (user is null || !await GoogleAuthRules.IsActiveAsync(accounts, user, ct)
            || !await accounts.FamilyIsActiveAsync(actorId, sessionFamilyId, now, ct)) return Unavailable();
        if (user.RegistrationExpiresAt.HasValue && !user.EmailVerifiedAt.HasValue)
            return AuthResult<GoogleLinkResponse>.Fail("EMAIL_NOT_VERIFIED", "Verify your local email before linking Google.", 403);
        if (user.PasswordHash != snapshot.PasswordHash)
            return AuthResult<GoogleLinkResponse>.Fail("ACCOUNT_CHANGED", "Password changed concurrently. Sign in again.", 409);
        var owner = await accounts.FindUserByFirebaseUidAsync(identity.Uid, ct);
        if (owner is not null && owner.Id != actorId)
            return AuthResult<GoogleLinkResponse>.Fail("GOOGLE_ALREADY_LINKED", "Google identity belongs to another account.", 409);
        if (user.FirebaseUid is not null && user.FirebaseUid != identity.Uid)
            return AuthResult<GoogleLinkResponse>.Fail("GOOGLE_ACCOUNT_REPLACEMENT_FORBIDDEN", "A different Google identity is already linked.", 409);
        if (user.FirebaseUid == identity.Uid)
            return AuthResult<GoogleLinkResponse>.Ok(new(GoogleAuthRules.ToAccount(user), true, false));
        if (await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM public.password_reset_operations WHERE user_id={actorId} AND status='Pending') AS "Value"
            """).SingleAsync(ct))
            return AuthResult<GoogleLinkResponse>.Fail("PASSWORD_RESET_PENDING", "Wait for password recovery to finish before linking Google.", 409);
        try
        {
            await db.Users.Where(x => x.Id == actorId).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FirebaseUid, identity.Uid).SetProperty(x => x.ProfileRevision, x => x.ProfileRevision + 1)
                .SetProperty(x => x.UpdatedAt, now), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "users_firebase_uid_key")
        { return AuthResult<GoogleLinkResponse>.Fail("GOOGLE_ALREADY_LINKED", "Google identity belongs to another account.", 409); }
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.local_password_reset_tokens SET used_at={now} WHERE user_id={actorId} AND used_at IS NULL", ct);
        await accounts.InvalidateUserResetTokensAsync(actorId, ct);
        await accounts.RevokeAllUserSessionsAsync(actorId, now, ct);
        await accounts.WriteAuditAsync(user, "Update", actorId, now, ct);
        user.FirebaseUid = identity.Uid; user.ProfileRevision++; user.UpdatedAt = now;
        await tx.CommitAsync(ct);
        return AuthResult<GoogleLinkResponse>.Ok(new(GoogleAuthRules.ToAccount(user), false, true));
    }

    private static AuthResult<GoogleLinkResponse> Unavailable() =>
        AuthResult<GoogleLinkResponse>.Fail("UNAUTHORIZED", "Account, organization or session is unavailable.", 401);
}
