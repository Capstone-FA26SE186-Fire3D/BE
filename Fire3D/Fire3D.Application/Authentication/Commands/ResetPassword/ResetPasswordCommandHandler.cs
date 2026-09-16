using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.ResetPassword;

internal sealed class ResetPasswordCommandHandler(
    IAuthStore store,
    IPasswordService passwords,
    TimeProvider clock)
    : IRequestHandler<ResetPasswordCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(ResetPasswordCommand command, CancellationToken ct)
    {
        // Validate password length trước khi hit DB
        if (command.NewPassword is null || command.NewPassword.Length is < 12 or > 128
            || string.IsNullOrWhiteSpace(command.NewPassword))
            return AuthResult<bool>.Fail("VALIDATION_ERROR", "Password must be 12–128 characters.", 400);

        var resetToken = await store.FindValidResetTokenAsync(command.Token, ct);
        if (resetToken is null)
            return AuthResult<bool>.Fail("INVALID_TOKEN", "Token không hợp lệ hoặc đã hết hạn.", 400);

        var user = await store.FindUserAsync(resetToken.UserId, ct);
        if (user is null || !user.IsActive || user.DeletedAt.HasValue)
            return AuthResult<bool>.Fail("INVALID_TOKEN", "Token không hợp lệ hoặc đã hết hạn.", 400);

        var now = AuthSupport.UtcNow(clock);
        var newHash = passwords.Hash(user, command.NewPassword);

        // Đổi mật khẩu + đánh dấu token đã dùng + xoá các token còn lại
        await store.UpdateLoginAsync(user.Id, now, newHash, ct);
        await store.MarkResetTokenUsedAsync(resetToken.Id, now, ct);
        await store.InvalidateUserResetTokensAsync(user.Id, ct);

        return AuthResult<bool>.Ok(true);
    }
}
