using System.Net.Mail;
using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;

namespace Fire3D.Application.Authentication.Internal;

internal static class AuthSupport
{
    internal static async Task<AuthResult<AccountResponse>> CreateAsync(IAuthStore store,
        IPasswordService passwords, TimeProvider clock, CreateAccountRequest request, User? actor, CancellationToken ct, Guid? correlationId = null)
    {
        var email = NormalizeEmail(request.Email);
        var username = request.Role == UserRole.Trainee
            ? SelfRegistrationValidation.NormalizeUsername(request.Username)
            : request.Username is null ? null : SelfRegistrationValidation.NormalizeUsername(request.Username);
        if (email is null || request.Password is null || request.Password.Length is < 6 or > 128
            || string.IsNullOrWhiteSpace(request.Password) || request.FullName?.Length > 200
            || request.Role is null || !Enum.IsDefined(request.Role.Value)
            || (request.Role == UserRole.Trainee && username is null)
            || (request.Role != UserRole.Trainee && request.Username is not null && username is null))
            return AuthResult<AccountResponse>.Fail("VALIDATION_ERROR", "Use a valid email, a 6-128 character password, and a valid role/name.", 400);
        if ((request.Role == UserRole.OrganizationUser) != request.OrganizationId.HasValue)
            return AuthResult<AccountResponse>.Fail("INVALID_ORGANIZATION", "Only OrganizationUser must have an organization.", 400);
        var now = UtcNow(clock);
        var user = new User
        {
            Id = Guid.NewGuid(), Email = email, FullName = request.FullName?.Trim(),
            Role = request.Role.Value, OrganizationId = request.OrganizationId, Username = username,
            IsActive = true, CreatedAt = now, UpdatedAt = now
        };
        user.PasswordHash = passwords.Hash(user, request.Password);
        await using var transaction = actor is null ? null : await store.BeginUserTransactionAsync(actor.Id, ct);
        if (actor is not null)
        {
            var currentActor = await store.FindUserAsync(actor.Id, ct);
            if (currentActor is null || !await IsActiveAsync(store, currentActor, ct) || currentActor.Role != UserRole.PlatformAdmin)
                return AuthResult<AccountResponse>.Fail("FORBIDDEN", "PlatformAdmin is required.", 403);
        }
        if (request.OrganizationId is Guid org && !await store.OrganizationIsActiveAsync(org, ct))
            return AuthResult<AccountResponse>.Fail("INVALID_ORGANIZATION", "Organization is unavailable.", 400);
        if (user.Role == UserRole.Trainee)
        {
            var conflict = await store.TryCreateTraineeAsync(user, ct);
            if (conflict != RegisterConflict.None)
                return SelfRegistrationValidation.Conflict<AccountResponse>(conflict);
        }
        else if (!await store.TryCreateUserAsync(user, ct))
            return AuthResult<AccountResponse>.Fail("EMAIL_EXISTS", "Email is already registered.", 409);
        await store.WriteAuditAsync(actor ?? user, "Create", user.Id, now, ct, correlationId);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return AuthResult<AccountResponse>.Ok(ToAccount(user));
    }

    internal static async Task<TokenResponse> IssueAsync(IAuthStore store, ITokenService tokens,
        User user, Guid familyId, DateTime now, DateTime refreshExpiresAt, CancellationToken ct)
    {
        var raw = tokens.CreateRefreshToken();
        await store.AddRefreshTokenAsync(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, FamilyId = familyId,
            TokenHash = tokens.HashRefreshToken(raw), CreatedAt = now, ExpiresAt = refreshExpiresAt
        }, ct);
        var access = tokens.CreateAccessToken(user, familyId, now);
        return new(access.Value, raw, ToAccount(user));
    }

    internal static Task<bool> IsActiveAsync(IAuthStore store, User user, CancellationToken ct) =>
        !user.IsActive || user.DeletedAt.HasValue || !Enum.IsDefined(user.Role)
        || ((user.Role == UserRole.OrganizationUser) != user.OrganizationId.HasValue)
            ? Task.FromResult(false)
            : user.OrganizationId is Guid org ? store.OrganizationIsActiveAsync(org, ct) : Task.FromResult(true);

    internal static DateTime UtcNow(TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
    }

    internal static AccountResponse ToAccount(User user) => new(user.Id, user.Email, user.FullName, user.Role, user.OrganizationId,
        user.Username, user.Dob, user.Gender, user.PhoneNumber, null, user.IsActive,
        user.LastLoginAt, user.CreatedAt, user.UpdatedAt, user.EmailVerifiedAt, user.RegistrationExpiresAt, user.ProfileRevision);

    internal static bool IsPendingEmailVerification(User user) =>
        user.RegistrationExpiresAt.HasValue && !user.EmailVerifiedAt.HasValue;

    internal static bool RegistrationHasExpired(User user, DateTime now) =>
        IsPendingEmailVerification(user) && user.RegistrationExpiresAt <= now;

    internal static AuthResult<TokenResponse> InvalidCredentials() => AuthResult<TokenResponse>.Fail("INVALID_CREDENTIALS", "Invalid email or password.", 401);
    internal static AuthResult<TokenResponse> InvalidRefresh() => AuthResult<TokenResponse>.Fail("INVALID_REFRESH_TOKEN", "Refresh token is invalid or expired.", 401);

    internal static string? NormalizeEmail(string? input)
    {
        var value = input?.Trim().ToLowerInvariant();
        return value is not null && value.Length <= 254 && MailAddress.TryCreate(value, out var address)
            && address.Address == value && value.Contains('@') ? value : null;
    }
}
