using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Users.Commands.RegisterDevice;

public sealed record RevokeDeviceCommand(Guid UserId, Guid SessionFamilyId, string DeviceUuid, string? InstallationKey) : IRequest<AuthResult<bool>>;

public sealed class RevokeDeviceCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<RevokeDeviceCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(RevokeDeviceCommand request, CancellationToken ct)
    {
        if (request.UserId == Guid.Empty || request.SessionFamilyId == Guid.Empty || !Guid.TryParse(request.DeviceUuid, out _) || !DeviceRegistrationValidation.TryValidInstallationKey(request.InstallationKey))
            return AuthResult<bool>.Fail("VALIDATION_ERROR", "A valid device UUID and X-Installation-Key are required.", 400);

        var canonicalDeviceUuid = Guid.Parse(request.DeviceUuid).ToString("D");
        var now = clock.GetUtcNow().UtcDateTime;
        await using var transaction = await store.BeginUserTransactionAsync(request.UserId, ct);
        var user = await store.FindUserAsync(request.UserId, ct);
        if (user is null || !await AuthSupport.IsActiveAsync(store, user, ct) || !await store.FamilyIsActiveAsync(user.Id, request.SessionFamilyId, now, ct))
            return AuthResult<bool>.Fail("UNAUTHORIZED", "Session is unavailable.", 401);
        var result = await store.RevokeDeviceAsync(request.UserId, canonicalDeviceUuid, DeviceRegistrationValidation.HashInstallationKey(request.InstallationKey!), now, ct);
        if (result == DeviceRevokeResult.InstallationKeyMismatch)
            return AuthResult<bool>.Fail("INSTALLATION_KEY_INVALID", "The installation key does not match this device.", 403);
        await transaction.CommitAsync(ct);
        return AuthResult<bool>.Ok(true);
    }
}
