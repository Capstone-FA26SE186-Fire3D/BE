using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;


using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Authentication;

public sealed class GoogleOnboardingService(Fire3DDbContext db, IAuthStore accounts, TimeProvider clock) : IGoogleOnboardingService
{
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<AuthResult<GoogleOnboardingProof>> BeginAsync(VerifiedIdentity identity, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(identity.Email);
        if (email is null || string.IsNullOrWhiteSpace(identity.Uid) || identity.Uid.Length > 128)
            return AuthResult<GoogleOnboardingProof>.Fail("INVALID_FIREBASE_TOKEN", "Verified Google identity is required.", 401);
        await using var tx = await GoogleIdentityTransactions.BeginAsync(db, identity.Uid, null, ct);
        if (await accounts.FindUserByFirebaseUidAsync(identity.Uid, ct) is not null)
            return AuthResult<GoogleOnboardingProof>.Fail("ACCOUNT_CHANGED", "Sign in again with Google.", 409);
        if (await accounts.FindUserByEmailAsync(email, ct) is not null)
            return AuthResult<GoogleOnboardingProof>.Fail("ACCOUNT_LINK_REQUIRED", "Sign in locally before linking Google.", 409);
        var now = GoogleAuthRules.UtcNow(clock);
        // Bound proof creation for one verified UID; existing proofs are not extended or revoked.
        var recent = await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM public.auth_google_onboarding_sessions WHERE firebase_uid={identity.Uid} AND created_at>{now.AddMinutes(-15)}").SingleAsync(ct);
        if (recent >= 10)
            return AuthResult<GoogleOnboardingProof>.Fail("GOOGLE_ONBOARDING_RATE_LIMITED", "Too many onboarding attempts. Try again later.", 429);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = now.AddMinutes(15);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.auth_google_onboarding_sessions(id,firebase_uid,email,onboarding_token_hash,created_at,expires_at)
            VALUES({Guid.NewGuid()},{identity.Uid},{email},{Hash(token)},{now},{expires})
            """, ct);
        await tx.CommitAsync(ct);
        return AuthResult<GoogleOnboardingProof>.Ok(new(token, expires));
    }

    public async Task<AuthResult<GoogleOnboardingCompletion>> CompleteAsync(GoogleOnboardingCompleteRequest request, CancellationToken ct)
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
        var userId = initial.CompletedUserId ?? Guid.NewGuid();
        await using var tx = await GoogleIdentityTransactions.BeginAsync(db, initial.Uid, userId, ct);
        var row = await ReadAsync(tokenHash, ct);
        if (row is null || row.Uid != initial.Uid || row.Email != initial.Email) return InvalidProof();
        var now = GoogleAuthRules.UtcNow(clock);
        if (row.CompletedAt.HasValue)
        {
            if (row.CompletedInputHash != inputHash)
                return AuthResult<GoogleOnboardingCompletion>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Onboarding was completed with different information.", 409);
            if (row.CompletedAt < now.AddHours(-24)) return InvalidProof();
            // Another completion may have won before this transaction; UID lock protects its immutable result.
            var completed = await accounts.FindUserAsync(row.CompletedUserId!.Value, ct);
            if (completed is null || completed.FirebaseUid != row.Uid || !await GoogleAuthRules.IsActiveAsync(accounts, completed, ct))
                return AuthResult<GoogleOnboardingCompletion>.Fail("ACCOUNT_DISABLED", "Account or organization is unavailable.", 403);
            return AuthResult<GoogleOnboardingCompletion>.Ok(new(GoogleAuthRules.ToAccount(completed), true));
        }
        if (row.ExpiresAt <= now) return InvalidProof();
        if (await accounts.FindUserByFirebaseUidAsync(row.Uid, ct) is not null)
            return AuthResult<GoogleOnboardingCompletion>.Fail("ACCOUNT_CHANGED", "Google identity has already been registered. Sign in again.", 409);
        if (await accounts.FindUserByEmailAsync(row.Email, ct) is not null)
            return AuthResult<GoogleOnboardingCompletion>.Fail("ACCOUNT_LINK_REQUIRED", "Sign in locally before linking Google.", 409);
        var user = new User { Id = userId, FirebaseUid = row.Uid, Email = row.Email, Role = request.AccountType,
            Username = input.Username, FullName = input.FullName, Dob = input.Dob, Gender = input.Gender, PhoneNumber = input.PhoneNumber,
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
        if (conflict != RegisterConflict.None) return GoogleAuthRules.Conflict<GoogleOnboardingCompletion>(conflict);
        var consumed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.auth_google_onboarding_sessions SET requested_role={request.AccountType.ToString()}::public.user_role_enum,
             completed_user_id={user.Id},completed_input_hash={inputHash},completed_at={now}
             WHERE id={row.Id} AND completed_at IS NULL
            """, ct);
        if (consumed != 1) throw new InvalidOperationException("Onboarding receipt was not committed.");
        await accounts.WriteAuditAsync(user, "Create", user.Id, now, ct);
        await tx.CommitAsync(ct);
        return AuthResult<GoogleOnboardingCompletion>.Ok(new(GoogleAuthRules.ToAccount(user), false));
    }

    private Task<OnboardingRow?> ReadAsync(string tokenHash, CancellationToken ct) => db.Database.SqlQuery<OnboardingRow>($"""
        SELECT id AS "Id",firebase_uid AS "Uid",email AS "Email",expires_at AS "ExpiresAt",
         completed_at AS "CompletedAt",completed_user_id AS "CompletedUserId",completed_input_hash AS "CompletedInputHash"
         FROM public.auth_google_onboarding_sessions WHERE onboarding_token_hash={tokenHash}
        """).SingleOrDefaultAsync(ct);
    private static AuthResult<GoogleOnboardingCompletion> InvalidProof() => AuthResult<GoogleOnboardingCompletion>.Fail(
        "INVALID_ONBOARDING_TOKEN", "Onboarding token is invalid or expired. Sign in with Google again.", 400);

    public sealed class OnboardingRow
    {
        public Guid Id { get; set; }
        public string Uid { get; set; } = "";
        public string Email { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public Guid? CompletedUserId { get; set; }
        public string? CompletedInputHash { get; set; }
    }
}
