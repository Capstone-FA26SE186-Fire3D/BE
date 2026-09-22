using MediatR;
namespace Fire3D.Application.Authentication.Commands.ForgotPassword;
public sealed record ForgotPasswordCommand(string Email) : IRequest<AuthResult<bool>>;
public sealed class ForgotPasswordCommandHandler(IAuthStore authStore) : IRequestHandler<ForgotPasswordCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(ForgotPasswordCommand request, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(request.Email);
        if (email is null) return AuthResult<bool>.Fail("INVALID_EMAIL", "Use a valid email address (maximum 254 characters).", 400);
        await authStore.EnqueuePasswordResetAsync(email, ct);
        return AuthResult<bool>.Ok(true);
    }
}
