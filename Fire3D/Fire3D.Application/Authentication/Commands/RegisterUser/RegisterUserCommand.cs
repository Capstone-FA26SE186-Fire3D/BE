using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using Fire3D.Domain.Entities;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

public sealed record RegisterUserCommand(string Email, string Password, string FullName) : IRequest<AuthResult<AccountResponse>>;

public sealed class RegisterUserCommandHandler(IAuthStore store, IPasswordService passwords, TimeProvider clock)
    : IRequestHandler<RegisterUserCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(RegisterUserCommand command, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(command.Email);
        if (email is null || !PasswordResetValidation.ValidPassword(command.Password)
            || string.IsNullOrWhiteSpace(command.FullName) || command.FullName.Length > 200)
            return AuthResult<AccountResponse>.Fail("VALIDATION_ERROR", "Use a valid email, a 12–128 character password and a name of 1–200 characters.", 400);
        if (await store.FindUserByEmailAsync(email, ct) is not null)
            return AuthResult<AccountResponse>.Fail("EMAIL_EXISTS", "Email is already registered.", 409);
        var now = AuthSupport.UtcNow(clock);
        var user = new User {
            Id = Guid.NewGuid(), Email = email, FullName = command.FullName.Trim(),
            Role = UserRole.Trainee, IsActive = true, CreatedAt = now, UpdatedAt = now
        };
        user.PasswordHash = passwords.Hash(user, command.Password);
        await using var transaction = await store.BeginUserTransactionAsync(user.Id, ct);
        if (!await store.TryCreateUserAsync(user, ct))
            return AuthResult<AccountResponse>.Fail("EMAIL_EXISTS", "Email is already registered.", 409);
        await store.WriteAuditAsync(user, "Create", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<AccountResponse>.Ok(AuthSupport.ToAccount(user));
    }
}
