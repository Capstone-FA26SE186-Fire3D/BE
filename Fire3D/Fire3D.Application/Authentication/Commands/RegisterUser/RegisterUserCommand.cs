using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using Fire3D.Domain.Entities;
using MediatR;
using FirebaseAdmin.Auth;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

public sealed record RegisterUserCommand(string Email, string Password, string FullName) : IRequest<AuthResult<AccountResponse>>;

internal sealed class RegisterUserCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<RegisterUserCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(RegisterUserCommand command, CancellationToken ct)
    {
        // 1. Validation
        var email = AuthSupport.NormalizeEmail(command.Email);
        if (email is null || command.Password is null || command.Password.Length is < 12 or > 128
            || string.IsNullOrWhiteSpace(command.Password) || command.FullName?.Length > 200)
        {
            return AuthResult<AccountResponse>.Fail("VALIDATION_ERROR", "Vui lòng dùng email h?p l?, m?t kh?u 12-128 ký t? và tên h?p l?.", 400);
        }

        // 2. Check early if email already exists in DB to avoid creating orphaned Firebase users
        if (await store.FindUserByEmailAsync(email, ct) != null)
        {
            return AuthResult<AccountResponse>.Fail("EMAIL_EXISTS", "Email này dã du?c dang ký.", 409);
        }

        // 3. Create Firebase User
        UserRecord userRecord;
        try
        {
            var userArgs = new UserRecordArgs
            {
                Email = email,
                Password = command.Password,
                DisplayName = command.FullName?.Trim()
            };
            userRecord = await FirebaseAuth.DefaultInstance.CreateUserAsync(userArgs, ct);
        }
        catch (FirebaseAuthException ex)
        {
            return AuthResult<AccountResponse>.Fail("FIREBASE_ERROR", ex.Message, 400);
        }

        var userId = Guid.NewGuid();
        var now = AuthSupport.UtcNow(clock);
        var user = new User
        {
            Id = userId,
            Email = email,
            FullName = command.FullName?.Trim(),
            Role = UserRole.Trainee,
            FirebaseUid = userRecord.Uid,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        // 4. Create Postgres User in a transaction
        await using var transaction = await store.BeginUserTransactionAsync(userId, ct);
        
        if (!await store.TryCreateUserAsync(user, ct))
        {
            // Compensation: Delete Firebase user if DB insert failed
            await FirebaseAuth.DefaultInstance.DeleteUserAsync(userRecord.Uid);
            return AuthResult<AccountResponse>.Fail("EMAIL_EXISTS", "Email này dã du?c dang ký.", 409);
        }

        // 5. Write audit and commit
        await store.WriteAuditAsync(user, "Create", user.Id, now, ct, null);
        await transaction.CommitAsync(ct);

        return AuthResult<AccountResponse>.Ok(AuthSupport.ToAccount(user));
    }
}
