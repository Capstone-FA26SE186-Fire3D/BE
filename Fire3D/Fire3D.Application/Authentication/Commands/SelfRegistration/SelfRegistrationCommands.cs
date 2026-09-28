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
        var errors = SelfRegistrationValidation.ValidateTrainee(command, email, username, fullName);
        if (errors.Count != 0) return SelfRegistrationValidation.Invalid<AccountResponse>(errors);

        var now = AuthSupport.UtcNow(clock);
        var user = new User
        {
            Id = Guid.NewGuid(), Email = email!, Username = username!, FullName = fullName,
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
        var errors = SelfRegistrationValidation.ValidateOrganization(command, email, fullName, organizationName, address, phone);
        if (errors.Count != 0) return SelfRegistrationValidation.Invalid<AccountResponse>(errors);

        var now = AuthSupport.UtcNow(clock);
        var organization = new Organization
        {
            Id = Guid.NewGuid(), Name = organizationName!, Slug = "org-" + Guid.NewGuid().ToString("N"),
            Address = address!, PhoneNumber = phone!, Plan = "free", Metadata = "{}", IsActive = true,
            CreatedAt = now, UpdatedAt = now
        };
        var user = new User
        {
            Id = Guid.NewGuid(), Email = email!, FullName = fullName, Role = UserRole.OrganizationUser,
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
        if (string.IsNullOrWhiteSpace(value) || value.Length > 50 || value.Count(x => x == '+') > 1
            || (value.Contains('+') && !value.StartsWith('+'))
            || value.Any(character => !(character is >= '0' and <= '9') && character is not '+' and not '-' and not ' ' and not '(' and not ')'))
            return null;
        var digits = new string(value.Where(character => character is >= '0' and <= '9').ToArray());
        return digits.Length is >= 6 and <= 15 ? (value.StartsWith('+') ? "+" : string.Empty) + digits : null;
    }

    internal static bool PasswordsMatch(string? password, string? confirmation) =>
        PasswordResetValidation.ValidPassword(password) && string.Equals(password, confirmation, StringComparison.Ordinal);

    internal static Dictionary<string, string[]> ValidateTrainee(RegisterTraineeCommand command, string? email, string? username, string? fullName)
    {
        var errors = new Dictionary<string, string[]>();
        Add(errors, "email", email is null, "Email không hợp lệ hoặc vượt quá 254 ký tự.");
        Add(errors, "username", username is null, "Username phải dài 3–30 ký tự, chỉ gồm a-z, số, dấu chấm, gạch dưới hoặc gạch ngang.");
        Add(errors, "password", !PasswordResetValidation.ValidPassword(command.Password), "Mật khẩu phải dài 12–128 ký tự và không chỉ gồm khoảng trắng.");
        Add(errors, "confirmPassword", !string.Equals(command.Password, command.ConfirmPassword, StringComparison.Ordinal), "Mật khẩu xác nhận không khớp.");
        Add(errors, "fullName", command.FullName is not null && fullName is null, "Họ tên không được rỗng và tối đa 200 ký tự.");
        return errors;
    }

    internal static Dictionary<string, string[]> ValidateOrganization(RegisterOrganizationCommand command, string? email, string? fullName,
        string? organizationName, string? address, string? phone)
    {
        var errors = new Dictionary<string, string[]>();
        Add(errors, "email", email is null, "Email không hợp lệ hoặc vượt quá 254 ký tự.");
        Add(errors, "password", !PasswordResetValidation.ValidPassword(command.Password), "Mật khẩu phải dài 12–128 ký tự và không chỉ gồm khoảng trắng.");
        Add(errors, "confirmPassword", !string.Equals(command.Password, command.ConfirmPassword, StringComparison.Ordinal), "Mật khẩu xác nhận không khớp.");
        Add(errors, "fullName", command.FullName is not null && fullName is null, "Họ tên không được rỗng và tối đa 200 ký tự.");
        Add(errors, "organizationName", organizationName is null, "Tên tổ chức không được rỗng và tối đa 255 ký tự.");
        Add(errors, "organizationAddress", address is null, "Địa chỉ tổ chức không được rỗng và tối đa 2000 ký tự.");
        Add(errors, "organizationPhoneNumber", phone is null, "Số điện thoại phải có 6–15 chữ số; có thể bắt đầu bằng dấu +.");
        return errors;
    }

    private static void Add(Dictionary<string, string[]> errors, string field, bool invalid, string message)
    {
        if (invalid) errors[field] = [message];
    }

    internal static AuthResult<T> Invalid<T>(IReadOnlyDictionary<string, string[]> errors) =>
        AuthResult<T>.Fail("VALIDATION_ERROR", "Dữ liệu đăng ký không hợp lệ.", 400, errors);
    internal static AuthResult<T> Conflict<T>(RegisterConflict conflict) => conflict switch
    {
        RegisterConflict.UsernameTaken => AuthResult<T>.Fail("USERNAME_EXISTS", "Username is already registered.", 409),
        RegisterConflict.SlugTaken => AuthResult<T>.Fail("ORGANIZATION_CONFLICT", "Organization could not be created. Retry the request.", 409),
        _ => AuthResult<T>.Fail("EMAIL_EXISTS", "Email is already registered.", 409)
    };
}
