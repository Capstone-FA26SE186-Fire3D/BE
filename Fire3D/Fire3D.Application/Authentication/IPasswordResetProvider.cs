using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fire3D.Application.Authentication;

public record VerifiedResetIdentity(Guid UserId, string FirebaseUid);

public interface IPasswordResetProvider
{
    Task<string> GenerateResetLinkAsync(string email, CancellationToken ct);
    Task<VerifiedResetIdentity> VerifyResetCodeAsync(string oobCode, CancellationToken ct);
    Task ConfirmResetAsync(string oobCode, string newPassword, CancellationToken ct);
}
