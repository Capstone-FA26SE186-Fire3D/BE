using System.Text.RegularExpressions;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.SelfRegistration;

public sealed record RegisterTraineeCommand(string Email, string Username, string Password, string ConfirmPassword, string? FullName = null)
    : IRequest<AuthResult<AccountResponse>>;

public sealed record RegisterOrganizationCommand(
    string Email, string Password, string ConfirmPassword, string? FullName,
    string OrganizationName, string OrganizationAddress, string OrganizationPhoneNumber)
    : IRequest<AuthResult<AccountResponse>>;

public sealed class RegisterTraineeCommandHandler(IAuthStore store, IPasswordService passwords,
    IEmailVerificationQueue verificationQueue, TimeProvider clock)
    : IRequestHandler<RegisterTraineeCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(RegisterTraineeCommand command, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(command.Email);
        var username = SelfRegistrationValidation.NormalizeUsername(command.Username);
        var fullName = SelfRegistrationValidation.NormalizeName(command.FullName);
        if (email is null || username is null || !SelfRegistrationValidation.PasswordsMatch(command.Password, command.ConfirmPassword)
            || (command.FullName is not null && fullName is null))
            return SelfRegistrationValidation.Invalid<AccountResponse>();

        var now = AuthSupport.UtcNow(clock);
        var user = new User
        {
            Id = Guid.NewGuid(), Email = email, Username = username, FullName = fullName,
            Role = UserRole.Trainee, IsActive = true, CreatedAt = now, UpdatedAt = now,
            RegistrationExpiresAt = now.AddHours(2)
        };
        user.PasswordHash = passwords.Hash(user, command.Password);
        await using var transaction = await store.BeginUserTransactionAsync(user.Id, ct);
        var conflict = await store.TryCreateTraineeAsync(user, ct);
        if (conflict != RegisterConflict.None) return SelfRegistrationValidation.Conflict<AccountResponse>(conflict);
        await verificationQueue.EnqueueAsync(user.Id, user.Email, ct);
        await store.WriteAuditAsync(user, "Create", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<AccountResponse>.Ok(AuthSupport.ToAccount(user));
    }
}

public sealed class RegisterOrganizationCommandHandler(IAuthStore store, IPasswordService passwords,
    IEmailVerificationQueue verificationQueue, TimeProvider clock)
    : IRequestHandler<RegisterOrganizationCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(RegisterOrganizationCommand command, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(command.Email);
        var fullName = SelfRegistrationValidation.NormalizeName(command.FullName);
        var organizationName = SelfRegistrationValidation.NormalizeRequired(command.OrganizationName, 255);
        var address = SelfRegistrationValidation.NormalizeRequired(command.OrganizationAddress, 2_000);
        var phone = SelfRegistrationValidation.NormalizePhone(command.OrganizationPhoneNumber);
        if (email is null || organizationName is null || address is null || phone is null
            || !SelfRegistrationValidation.PasswordsMatch(command.Password, command.ConfirmPassword)
            || (command.FullName is not null && fullName is null))
            return SelfRegistrationValidation.Invalid<AccountResponse>();

        var now = AuthSupport.UtcNow(clock);
        var organization = new Organization
        {
            Id = Guid.NewGuid(), Name = organizationName, Slug = "org-" + Guid.NewGuid().ToString("N"),
            Address = address, PhoneNumber = phone, Plan = "free", Metadata = "{}", IsActive = true,
            CreatedAt = now, UpdatedAt = now
        };
        var user = new User
        {
            Id = Guid.NewGuid(), Email = email, FullName = fullName, Role = UserRole.OrganizationUser,
            OrganizationId = organization.Id, IsActive = true, CreatedAt = now, UpdatedAt = now,
            RegistrationExpiresAt = now.AddHours(2)
        };
        organization.RegistrationOwnerUserId = user.Id;
        user.PasswordHash = passwords.Hash(user, command.Password);
        await using var transaction = await store.BeginUserTransactionAsync(user.Id, ct);
        var conflict = await store.TryCreateOrganizationWithUserAsync(organization, user, ct);
        if (conflict != RegisterConflict.None) return SelfRegistrationValidation.Conflict<AccountResponse>(conflict);
        await verificationQueue.EnqueueAsync(user.Id, user.Email, ct);
        await store.WriteAuditAsync(user, "Create", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<AccountResponse>.Ok(AuthSupport.ToAccount(user));
    }
}

internal static class SelfRegistrationValidation
{
    private static readonly Regex Username = new("^[a-z0-9._-]{3,30}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static string? NormalizeUsername(string? input)
    {
        var value = input?.Trim().ToLowerInvariant();
        return value is not null && Username.IsMatch(value) ? value : null;
    }

    internal static string? NormalizeName(string? input) => input is null ? null : NormalizeRequired(input, 200);
    internal static string? NormalizeRequired(string? input, int maximumLength)
    {
        var value = input?.Trim();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength ? value : null;
    }

    internal static string? NormalizePhone(string? input)
    {
        var value = input?.Trim();
        return value is { Length: >= 6 and <= 50 }
            && value.All(character => char.IsDigit(character) || character is '+' or '-' or ' ' or '(' or ')') ? value : null;
    }

    internal static bool PasswordsMatch(string? password, string? confirmation) =>
        PasswordResetValidation.ValidPassword(password) && string.Equals(password, confirmation, StringComparison.Ordinal);

    internal static AuthResult<T> Invalid<T>() => AuthResult<T>.Fail("VALIDATION_ERROR", "Use valid registration details and matching passwords.", 400);
    internal static AuthResult<T> Conflict<T>(RegisterConflict conflict) => conflict switch
    {
        RegisterConflict.UsernameTaken => AuthResult<T>.Fail("USERNAME_EXISTS", "Username is already registered.", 409),
        RegisterConflict.SlugTaken => AuthResult<T>.Fail("ORGANIZATION_CONFLICT", "Organization could not be created. Retry the request.", 409),
        _ => AuthResult<T>.Fail("EMAIL_EXISTS", "Email is already registered.", 409)
    };
}
