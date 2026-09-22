using MediatR;
namespace Fire3D.Application.Authentication.Commands.ResetPassword;

public sealed record ResetPasswordCommand(string Token, string NewPassword) : IRequest<AuthResult<bool>>;
public sealed class ResetPasswordCommandHandler(ILocalPasswordReset resets)
    : IRequestHandler<ResetPasswordCommand,AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(ResetPasswordCommand request,CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length!=64 || !request.Token.All(Uri.IsHexDigit))
            return AuthResult<bool>.Fail("INVALID_RESET_CODE","Reset token is invalid or expired.",400);
        if (!PasswordResetValidation.ValidPassword(request.NewPassword))
            return AuthResult<bool>.Fail("INVALID_PASSWORD","Use a password of 12–128 characters.",400);
        return await resets.ResetAsync(request.Token,request.NewPassword,ct)
            ? AuthResult<bool>.Ok(true)
            : AuthResult<bool>.Fail("INVALID_RESET_CODE","Reset token is invalid, expired or already used.",400);
    }
}
