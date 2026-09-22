using Fire3D.Application.Authentication;
using MediatR;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Fire3D.Application.Authentication.Commands.ResetPassword;

public sealed record ResetPasswordCommand(string OobCode, string NewPassword) : IRequest;

public sealed class ResetPasswordCommandHandler(IPasswordResetProvider provider, IAuthStore authStore) : IRequestHandler<ResetPasswordCommand>
{
    public async Task Handle(ResetPasswordCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OobCode))
            throw new Exception("INVALID_RESET_CODE");
            
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
            throw new Exception("INVALID_CREDENTIALS");

        // Verify with Firebase
        var identity = await provider.VerifyResetCodeAsync(request.OobCode, ct);
        
        // Ensure user exists
        var user = await authStore.FindUserAsync(identity.UserId, ct);
        if (user == null || user.FirebaseUid != identity.FirebaseUid)
            throw new Exception("INVALID_RESET_CODE");

        // Confirm reset with Firebase
        await provider.ConfirmResetAsync(request.OobCode, request.NewPassword, ct);

        // Revoke Fire3D sessions
        await authStore.RevokeAllUserSessionsAsync(identity.UserId, DateTime.UtcNow, ct);

        // Write Audit
        await authStore.WriteAuditAsync(
            user, 
            ""PasswordReset"", 
            identity.UserId, 
            DateTime.UtcNow, 
            ct);
    }
}
