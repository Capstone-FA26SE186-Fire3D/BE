using MediatR;

namespace Fire3D.Application.Authentication.Commands.ChangePassword;

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ChangePasswordCommand(Guid ActorId, string CurrentPassword, string NewPassword)
    : IRequest<AuthResult<bool>>;

public sealed class ChangePasswordCommandHandler(ILocalPasswordReset passwords)
    : IRequestHandler<ChangePasswordCommand, AuthResult<bool>>
{
    public Task<AuthResult<bool>> Handle(ChangePasswordCommand request, CancellationToken ct)
    {
        if (request.ActorId == Guid.Empty)
            return Task.FromResult(AuthResult<bool>.Fail("UNAUTHORIZED", "Authenticated user context is required.", 401));
        if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > 128)
            return Task.FromResult(AuthResult<bool>.Fail(
                "INVALID_CURRENT_PASSWORD", "Current password is invalid.", 400));
        if (!PasswordResetValidation.ValidPassword(request.NewPassword))
            return Task.FromResult(AuthResult<bool>.Fail(
                "INVALID_PASSWORD", "Use a password of 12–128 characters.", 400));
        return passwords.ChangeAsync(request.ActorId, request.CurrentPassword, request.NewPassword, ct);
    }
}
