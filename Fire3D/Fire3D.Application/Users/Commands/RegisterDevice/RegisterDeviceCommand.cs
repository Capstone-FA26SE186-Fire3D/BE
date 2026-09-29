
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using MediatR;
using System.Security.Cryptography;
using System.Text;

namespace Fire3D.Application.Users.Commands.RegisterDevice;

public sealed record RegisterDeviceRequest(string DeviceUuid, string? FcmToken, string? DeviceModel, string? OsVersion, string? AppVersion);
public sealed record DeviceRegistrationResponse(string DeviceUuid, bool NotificationsEnabled);
public record RegisterDeviceCommand(Guid UserId, string DeviceUuid, string? InstallationKey, string? FcmToken, string? DeviceModel, string? OsVersion, string? AppVersion)
    : IRequest<AuthResult<DeviceRegistrationResponse>>;

public sealed class RegisterDeviceCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<RegisterDeviceCommand, AuthResult<DeviceRegistrationResponse>>
{
    public async Task<AuthResult<DeviceRegistrationResponse>> Handle(RegisterDeviceCommand request, CancellationToken ct)
    {
        var errors = DeviceRegistrationValidation.Validate(request);
        if (errors.Count != 0)
            return AuthResult<DeviceRegistrationResponse>.Fail("VALIDATION_ERROR", "Device registration data is invalid.", 400, errors);

        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.InstallationKey!))).ToLowerInvariant();
        await using var transaction = await store.BeginUserTransactionAsync(request.UserId, ct);
        var user = await store.FindUserAsync(request.UserId, ct);
        if (user is null || !await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<DeviceRegistrationResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);

        var result = await store.RegisterDeviceAsync(request.UserId, request.DeviceUuid, keyHash, request.FcmToken,
            request.DeviceModel, request.OsVersion, request.AppVersion, AuthSupport.UtcNow(clock), ct);
        if (result == DeviceRegistrationResult.InstallationKeyMismatch)
            return AuthResult<DeviceRegistrationResponse>.Fail("INSTALLATION_KEY_INVALID", "The installation key does not match this device.", 403);
        if (result == DeviceRegistrationResult.TokenAlreadyBound)
            return AuthResult<DeviceRegistrationResponse>.Fail("FCM_TOKEN_ALREADY_BOUND", "This push token is registered to another active installation.", 409);

        await transaction.CommitAsync(ct);
        return AuthResult<DeviceRegistrationResponse>.Ok(new(request.DeviceUuid, !string.IsNullOrWhiteSpace(request.FcmToken)));
    }
}
internal static class DeviceRegistrationValidation
{
    internal static Dictionary<string, string[]> Validate(RegisterDeviceCommand request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.UserId == Guid.Empty || !Guid.TryParse(request.DeviceUuid, out _)) errors["deviceUuid"] = ["Device UUID must be a valid UUID."];
        if (!TryValidInstallationKey(request.InstallationKey)) errors["installationKey"] = ["X-Installation-Key must be a base64url-encoded 32-byte secret."];
        if (InvalidText(request.FcmToken, 4096)) errors["fcmToken"] = ["FCM token must contain 1-4096 printable characters."];
        if (InvalidText(request.DeviceModel, 255)) errors["deviceModel"] = ["Device model must contain at most 255 printable characters."];
        if (InvalidText(request.OsVersion, 100)) errors["osVersion"] = ["OS version must contain at most 100 printable characters."];
        if (InvalidText(request.AppVersion, 100)) errors["appVersion"] = ["App version must contain at most 100 printable characters."];
        return errors;
    }

    private static bool TryValidInstallationKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) return false;
        try { return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=')).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool InvalidText(string? value, int max) => value is not null &&
        (value.Length == 0 || value.Length > max || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl));
}

