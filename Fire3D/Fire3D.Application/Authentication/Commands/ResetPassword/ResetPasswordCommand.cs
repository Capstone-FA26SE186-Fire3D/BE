using Fire3D.Application.Authentication;
using MediatR;
using System;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fire3D.Application.Authentication.Commands.ResetPassword;

public sealed record ResetPasswordCommand(string OobCode, string NewPassword) : IRequest<AuthResult<bool>>;

public sealed class ResetPasswordCommandHandler(IPasswordResetProvider provider, IAuthStore authStore) : IRequestHandler<ResetPasswordCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(ResetPasswordCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OobCode))
            return AuthResult<bool>.Fail("INVALID_RESET_CODE", "Invalid reset code.", 400);
            
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 12)
            return AuthResult<bool>.Fail("INVALID_PASSWORD", "Password must be at least 12 characters long.", 400);

        VerifiedResetIdentity identity;
        try {
            identity = await provider.VerifyResetCodeAsync(request.OobCode, ct);
        } catch (Exception) {
            return AuthResult<bool>.Fail("INVALID_RESET_CODE", "Invalid reset code.", 400);
        }
        
        var user = await authStore.FindUserAsync(identity.UserId, ct);
        if (user == null || user.FirebaseUid != identity.FirebaseUid)
            return AuthResult<bool>.Fail("INVALID_RESET_CODE", "Invalid reset code.", 400);

        await using var transaction = await authStore.BeginUserTransactionAsync(user.Id, ct);
        
        await authStore.RevokeAllUserSessionsAsync(user.Id, DateTime.UtcNow, ct);

        await authStore.WriteAuditAsync(
            user, 
            "Update", 
            user.Id, 
            DateTime.UtcNow, 
            ct);
            
        try {
            await provider.ConfirmResetAsync(request.OobCode, request.NewPassword, ct);
        } catch (Exception) {
            return AuthResult<bool>.Fail("PROVIDER_ERROR", "Failed to reset password.", 500);
        }

        await transaction.CommitAsync(ct);

        return AuthResult<bool>.Ok(true);
    }
}
