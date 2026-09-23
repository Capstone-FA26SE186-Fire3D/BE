using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using Fire3D.Domain.Entities;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

public sealed record RegisterUserCommand(string Email, string Password, string FullName,
    DateOnly? Dob = null, UserGender? Gender = null, string? PhoneNumber = null, string? AvatarUrl = null)
    : IRequest<AuthResult<AccountResponse>>;

public sealed class RegisterUserCommandHandler(IAuthStore store, IPasswordService passwords, IEmailVerificationQueue verificationQueue, TimeProvider clock)
    : IRequestHandler<RegisterUserCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(RegisterUserCommand command, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(command.Email);
        var phoneNumber = NormalizePhoneNumber(command.PhoneNumber);
        var avatarUrl = NormalizeAvatarUrl(command.AvatarUrl);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (email is null || !PasswordResetValidation.ValidPassword(command.Password)
            || string.IsNullOrWhiteSpace(command.FullName) || command.FullName.Length > 200
            || command.Dob > today || (command.Gender is not null && !Enum.IsDefined(command.Gender.Value))
            || (command.PhoneNumber is not null && phoneNumber is null)
            || (command.AvatarUrl is not null && avatarUrl is null))
            return AuthResult<AccountResponse>.Fail("VALIDATION_ERROR", "Use a valid email, a 12–128 character password and a name of 1–200 characters.", 400);
        if (await store.FindUserByEmailAsync(email, ct) is not null)
            return AuthResult<AccountResponse>.Fail("EMAIL_EXISTS", "Email is already registered.", 409);
        var now = AuthSupport.UtcNow(clock);
        var user = new User {
            Id = Guid.NewGuid(), Email = email, FullName = command.FullName.Trim(),
            Role = UserRole.Trainee, Dob = command.Dob, Gender = command.Gender,
            PhoneNumber = phoneNumber, AvatarUrl = avatarUrl,
            IsActive = true, CreatedAt = now, UpdatedAt = now
        };
        user.PasswordHash = passwords.Hash(user, command.Password);
        await using var transaction = await store.BeginUserTransactionAsync(user.Id, ct);
        if (!await store.TryCreateUserAsync(user, ct))
            return AuthResult<AccountResponse>.Fail("EMAIL_EXISTS", "Email is already registered.", 409);
        await verificationQueue.EnqueueAsync(user.Email, ct);
        await store.WriteAuditAsync(user, "Create", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<AccountResponse>.Ok(AuthSupport.ToAccount(user));
    }

    private static string? NormalizePhoneNumber(string? value)
    {
        if (value is null) return null;
        var phone = value.Trim();
        return phone.Length is >= 6 and <= 32 && phone.All(character => char.IsDigit(character) || character is '+' or '-' or ' ' or '(' or ')')
            ? phone : null;
    }

    private static string? NormalizeAvatarUrl(string? value)
    {
        if (value is null) return null;
        var url = value.Trim();
        return url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(parsed.UserInfo)
            ? url : null;
    }
}
