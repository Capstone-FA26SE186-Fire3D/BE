using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.ForgotPassword;

public sealed record ForgotPasswordCommand(string Email) : IRequest<AuthResult<bool>>;

public sealed class ForgotPasswordCommandHandler(IAuthStore authStore) : IRequestHandler<ForgotPasswordCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(ForgotPasswordCommand request, CancellationToken ct)
    {
                if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@') || request.Email.Length > 100)
            return AuthResult<bool>.Fail("INVALID_EMAIL", "Invalid email format.", 400);
            
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        await authStore.EnqueuePasswordResetAsync(normalizedEmail, ct);
        return AuthResult<bool>.Ok(true);
    }
}



