using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;


using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fire3D.Infrastructure.Authentication;

public sealed class GoogleOnboardingService(Fire3DDbContext db, IAuthStore accounts, TimeProvider clock, ITokenService tokens) : IGoogleOnboardingService
{
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<AuthResult<GoogleOnboardingProof>> BeginAsync(VerifiedIdentity identity, CancellationToken ct)
    {
        try { return await BeginCoreAsync(identity, ct); }
        catch (Exception e) when (IsLockTimeout(e))
        { return AuthResult<GoogleOnboardingProof>.Fail("ONBOARDING_RETRY_REQUIRED", "Onboarding is busy. Try again.", 503, retryAfterSeconds: 1); }
    }
    public async Task<AuthResult<GoogleOnboardingCompletion>> CompleteAsync(GoogleOnboardingCompleteRequest request, CancellationToken ct)
    {
        try { return await CompleteCoreAsync(request, ct); }
        catch (Exception e) when (IsLockTimeout(e))
        { return AuthResult<GoogleOnboardingCompletion>.Fail("ONBOARDING_RETRY_REQUIRED", "Onboarding is busy. Try again.", 503, retryAfterSeconds: 1); }
    }
    // Npgsql's execution strategy can wrap the EF update exception again. Only classify the exact SQL state.
    private static bool IsLockTimeout(Exception error) => error switch
    {
        PostgresException pg => pg.SqlState == PostgresErrorCodes.LockNotAvailable,
        DbUpdateException { InnerException: { } inner } => IsLockTimeout(inner),
        InvalidOperationException { InnerException: { } inner } => IsLockTimeout(inner),
        _ => false
    };
    private async Task<AuthResult<GoogleOnboardingProof>> BeginCoreAsync(VerifiedIdentity identity, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(identity.Email);
        if (email is null || string.IsNullOrWhiteSpace(identity.Uid) || identity.Uid.Length > 128)
            return AuthResult<GoogleOnboardingProof>.Fail("INVALID_FIREBASE_TOKEN", "Verified Google identity is required.", 401);
        await using var tx = await GoogleIdentityTransactions.BeginAsync(db, identity.Uid, null, ct, boundLockWait: true);
        if (await accounts.FindUserByFirebaseUidAsync(identity.Uid, ct) is not null)
            return AuthResult<GoogleOnboardingProof>.Fail("ACCOUNT_CHANGED", "Sign in again with Google.", 409);
        if (await accounts.FindUserByEmailAsync(email, ct) is not null)
            return AuthResult<GoogleOnboardingProof>.Fail("ACCOUNT_LINK_REQUIRED", "Sign in locally before linking Google.", 409);
        var now = GoogleAuthRules.UtcNow(clock);
        // Bound proof creation for one verified UID; existing proofs are not extended or revoked.
        var recent = await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM public.auth_google_onboarding_sessions WHERE firebase_uid={identity.Uid} AND created_at>{now.AddMinutes(-15)}").SingleAsync(ct);
        if (recent >= 10)
        {
            var oldest = await db.Database.SqlQuery<DateTime>($"SELECT min(created_at) AS \"Value\" FROM public.auth_google_onboarding_sessions WHERE firebase_uid={identity.Uid} AND created_at>{now.AddMinutes(-15)}").SingleAsync(ct);
            var retry = Math.Clamp((int)Math.Ceiling((oldest.AddMinutes(15) - now).TotalSeconds), 1, 900);
            return AuthResult<GoogleOnboardingProof>.Fail("GOOGLE_ONBOARDING_RATE_LIMITED", "Too many onboarding attempts. Try again later.", 429, retryAfterSeconds: retry);
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = now.AddMinutes(15);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.auth_google_onboarding_sessions(id,firebase_uid,email,display_name,onboarding_token_hash,created_at,expires_at)
            VALUES({Guid.NewGuid()},{identity.Uid},{email},{GoogleAuthRules.NormalizeDisplayName(identity.DisplayName)},{Hash(token)},{now},{expires})
            """, ct);
        await tx.CommitAsync(ct);
        return AuthResult<GoogleOnboardingProof>.Ok(new(token, expires));
    }

    private async Task<AuthResult<GoogleOnboardingCompletion>> CompleteCoreAsync(GoogleOnboardingCompleteRequest request, CancellationToken ct)
    {
        if (request.OnboardingToken is not { Length: 43 } || request.OnboardingToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return InvalidProof();
        var validated = GoogleAuthRules.Validate(request, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        if (!validated.IsSuccess) return AuthResult<GoogleOnboardingCompletion>.Fail(validated.Error!.Code, validated.Error.Message, validated.Error.Status, validated.Error.Errors);
        var input = validated.Value!;
        var inputHash = Hash(JsonSerializer.Serialize(input));
        var tokenHash = Hash(request.OnboardingToken);
        var initial = await ReadAsync(tokenHash, ct);
        if (initial is null) return InvalidProof();
        await using var tx = await GoogleIdentityTransactions.BeginAsync(db, initial.Uid, null, ct, boundLockWait: true);
        var row = await ReadAsync(tokenHash, ct);
        if (row is null || row.Uid != initial.Uid || row.Email != initial.Email) return InvalidProof();
        var userId = row.CompletedUserId ?? Guid.NewGuid();
        await GoogleIdentityTransactions.LockUserAsync(db, userId, ct);
        var now = GoogleAuthRules.UtcNow(clock);
        if (row.CompletedAt.HasValue)
        {
            if (row.CompletedInputHash != inputHash)
                return AuthResult<GoogleOnboardingCompletion>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Onboarding was completed with different information.", 409);
            if (row.CompletedAt <= now.AddHours(-24)) return ExpiredProof();
            // Another completion may have won before this transaction; UID lock protects its immutable result.
            var completed = await accounts.FindUserAsync(row.CompletedUserId!.Value, ct);
            if (completed is null || completed.FirebaseUid != row.Uid || !await GoogleAuthRules.IsActiveAsync(accounts, completed, ct))
                return AuthResult<GoogleOnboardingCompletion>.Fail("ACCOUNT_DISABLED", "Account or organization is unavailable.", 403);
            return AuthResult<GoogleOnboardingCompletion>.Fail("ONBOARDING_ALREADY_COMPLETED", "Onboarding is already complete. Exchange a valid Firebase ID token to sign in.", 409);
        }
        if (row.ExpiresAt <= now) return ExpiredProof();
        if (await accounts.FindUserByFirebaseUidAsync(row.Uid, ct) is not null)
            return AuthResult<GoogleOnboardingCompletion>.Fail("ACCOUNT_CHANGED", "Google identity has already been registered. Sign in again.", 409);
        if (await accounts.FindUserByEmailAsync(row.Email, ct) is not null)
            return LinkRequired();
        var user = new User { Id = userId, FirebaseUid = row.Uid, Email = row.Email, Role = request.AccountType,
            Username = input.Username, FullName = input.FullName ?? row.DisplayName, Dob = input.Dob, Gender = input.Gender, PhoneNumber = input.PhoneNumber,
            EmailVerifiedAt = now, IsActive = true, CreatedAt = now, UpdatedAt = now };
        RegisterConflict conflict;
        if (request.AccountType == UserRole.OrganizationUser)
        {
            var organization = new Organization { Id = Guid.NewGuid(), Name = input.OrganizationName!, Address = input.OrganizationAddress!, PhoneNumber = input.OrganizationPhoneNumber!,
                Slug = "org-" + Guid.NewGuid().ToString("N"), Plan = "free", Metadata = "{}", IsActive = true,
                RegistrationOwnerUserId = user.Id, CreatedAt = now, UpdatedAt = now };
            user.OrganizationId = organization.Id;
            conflict = await accounts.TryCreateOrganizationWithUserAsync(organization, user, ct);
        }
        else conflict = await accounts.TryCreateTraineeAsync(user, ct);
        if (conflict == RegisterConflict.EmailTaken)
            return LinkRequired();
        if (conflict != RegisterConflict.None) return GoogleAuthRules.Conflict<GoogleOnboardingCompletion>(conflict);
        var consumed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.auth_google_onboarding_sessions SET requested_role={request.AccountType.ToString()}::public.user_role_enum,
             completed_user_id={user.Id},completed_input_hash={inputHash},completed_at={now}
             WHERE id={row.Id} AND completed_at IS NULL
            """, ct);
        if (consumed != 1) throw new InvalidOperationException("Onboarding receipt was not committed.");
        await accounts.WriteAuditAsync(user, "Create", user.Id, now, ct);
        await accounts.UpdateLoginAsync(user.Id, now, ct);
        user.LastLoginAt = now; user.UpdatedAt = now;
        var authentication = await GoogleAuthRules.IssueSessionAsync(accounts, tokens, user, Guid.NewGuid(), now, ct);
        await accounts.WriteAuditAsync(user, "Login", user.Id, now, ct);
        await tx.CommitAsync(ct);
        return AuthResult<GoogleOnboardingCompletion>.Ok(new(authentication));
    }

    private Task<OnboardingRow?> ReadAsync(string tokenHash, CancellationToken ct) => db.Database.SqlQuery<OnboardingRow>($"""
        SELECT id AS "Id",firebase_uid AS "Uid",email AS "Email",display_name AS "DisplayName",expires_at AS "ExpiresAt",
         completed_at AS "CompletedAt",completed_user_id AS "CompletedUserId",completed_input_hash AS "CompletedInputHash"
         FROM public.auth_google_onboarding_sessions WHERE onboarding_token_hash={tokenHash}
        """).SingleOrDefaultAsync(ct);
    private static AuthResult<GoogleOnboardingCompletion> InvalidProof() => AuthResult<GoogleOnboardingCompletion>.Fail(
        "ONBOARDING_TOKEN_INVALID", "Onboarding token is invalid. Sign in with Google again.", 400);
    private static AuthResult<GoogleOnboardingCompletion> ExpiredProof() => AuthResult<GoogleOnboardingCompletion>.Fail(
        "ONBOARDING_TOKEN_EXPIRED", "Onboarding token has expired. Sign in with Google again.", 400);
    private static AuthResult<GoogleOnboardingCompletion> LinkRequired() => AuthResult<GoogleOnboardingCompletion>.Fail(
        "ACCOUNT_LINK_REQUIRED", "Sign in locally before linking Google.", 409,
        new Dictionary<string, string[]> { ["email"] = ["Email is already registered. Sign in locally before linking Google."] });

    public sealed class OnboardingRow
    {
        public Guid Id { get; set; }
        public string Uid { get; set; } = "";
        public string Email { get; set; } = "";
        public string? DisplayName { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public Guid? CompletedUserId { get; set; }
        public string? CompletedInputHash { get; set; }
    }
}
