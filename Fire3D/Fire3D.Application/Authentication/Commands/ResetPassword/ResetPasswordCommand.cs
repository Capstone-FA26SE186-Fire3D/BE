using System.Security.Cryptography;
using System.Text;
using MediatR;
namespace Fire3D.Application.Authentication.Commands.ResetPassword;
public sealed record ResetPasswordCommand(string OobCode, string NewPassword) : IRequest<AuthResult<bool>>;
public sealed class ResetPasswordCommandHandler(IPasswordResetProvider provider, IAuthStore accounts, IPasswordResetStore resets)
    : IRequestHandler<ResetPasswordCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(ResetPasswordCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OobCode) || request.OobCode.Length > 4096)
            return AuthResult<bool>.Fail("INVALID_RESET_CODE", "A valid reset code is required.", 400);
        if (!PasswordResetValidation.ValidPassword(request.NewPassword))
            return AuthResult<bool>.Fail("INVALID_PASSWORD", "Use a password of 12–128 characters.", 400);
        VerifiedResetIdentity identity;
        try { identity = await provider.VerifyResetCodeAsync(request.OobCode, ct); }
        catch (PasswordResetException ex) { return new(default, ex.Error); }
        var user = await accounts.FindUserAsync(identity.UserId, ct);
        if (user is null || user.FirebaseUid != identity.FirebaseUid || !user.IsActive || user.DeletedAt.HasValue)
            return new(default, PasswordResetException.InvalidCode().Error);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.OobCode))).ToLowerInvariant();
        var operation = await resets.BeginAsync(user.Id, identity.FirebaseUid, hash, ct);
        if (operation is null)
            return AuthResult<bool>.Fail("RESET_PENDING", "A reset is already recorded for this account or code. Recovery may be required.", 409);
        // No open DB transaction here. Any timeout/cancellation leaves the durable fence in place.
        try { await provider.ConfirmResetAsync(request.OobCode, request.NewPassword, ct); }
        catch (PasswordResetException ex) when (ex.Error.Status == 400)
        {
            await resets.FinishAsync(operation.Value, user.Id, "Rejected", ct);
            return new(default, ex.Error);
        }
        catch (PasswordResetException ex) { return new(default, ex.Error); }
        await resets.FinishAsync(operation.Value, user.Id, "Completed", ct);
        return AuthResult<bool>.Ok(true);
    }
}
