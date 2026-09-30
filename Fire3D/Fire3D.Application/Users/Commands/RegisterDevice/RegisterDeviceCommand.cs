
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using MediatR;
using System.Security.Cryptography;
using System.Text;

namespace Fire3D.Application.Users.Commands.RegisterDevice;

public sealed record RegisterDeviceRequest(string DeviceUuid, string? FcmToken, string? DeviceModel, string? OsVersion, string? AppVersion);
public sealed record DeviceRegistrationResponse(string DeviceUuid, bool NotificationsEnabled);
public record RegisterDeviceCommand(Guid UserId, Guid SessionFamilyId, string DeviceUuid, string? InstallationKey, string? FcmToken, string? DeviceModel, string? OsVersion, string? AppVersion)
    : IRequest<AuthResult<DeviceRegistrationResponse>>;

public sealed class RegisterDeviceCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<RegisterDeviceCommand, AuthResult<DeviceRegistrationResponse>>
{
    public async Task<AuthResult<DeviceRegistrationResponse>> Handle(RegisterDeviceCommand request, CancellationToken ct)
    {
        var errors = DeviceRegistrationValidation.Validate(request);
        if (errors.Count != 0)
            return AuthResult<DeviceRegistrationResponse>.Fail("VALIDATION_ERROR", "Device registration data is invalid.", 400, errors);

        var canonicalDeviceUuid = Guid.Parse(request.DeviceUuid).ToString("D");
        var keyHash = DeviceRegistrationValidation.HashInstallationKey(request.InstallationKey!);
        await using var transaction = await store.BeginUserTransactionAsync(request.UserId, ct);
        var user = await store.FindUserAsync(request.UserId, ct);
        if (user is null || !await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<DeviceRegistrationResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        var now = AuthSupport.UtcNow(clock);
        if (!await store.FamilyIsActiveAsync(user.Id, request.SessionFamilyId, now, ct))
            return AuthResult<DeviceRegistrationResponse>.Fail("UNAUTHORIZED", "Session is unavailable.", 401);

        var result = await store.RegisterDeviceAsync(request.UserId, canonicalDeviceUuid, keyHash, request.FcmToken,
            request.DeviceModel, request.OsVersion, request.AppVersion, now, ct);
        if (result == DeviceRegistrationResult.InstallationKeyMismatch)
            return AuthResult<DeviceRegistrationResponse>.Fail("INSTALLATION_KEY_INVALID", "The installation key does not match this device.", 403);
        if (result == DeviceRegistrationResult.TokenAlreadyBound)
            return AuthResult<DeviceRegistrationResponse>.Fail("FCM_TOKEN_ALREADY_BOUND", "This push token is registered to another active installation.", 409);

        await transaction.CommitAsync(ct);
        return AuthResult<DeviceRegistrationResponse>.Ok(new(canonicalDeviceUuid, !string.IsNullOrWhiteSpace(request.FcmToken)));
    }
}
internal static class DeviceRegistrationValidation
{
    internal static Dictionary<string, string[]> Validate(RegisterDeviceCommand request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.UserId == Guid.Empty || request.SessionFamilyId == Guid.Empty || !Guid.TryParse(request.DeviceUuid, out _)) errors["deviceUuid"] = ["Device UUID must be a valid UUID."];
        if (!TryValidInstallationKey(request.InstallationKey)) errors["installationKey"] = ["X-Installation-Key must be a base64url-encoded 32-byte secret."];
        if (InvalidFcmToken(request.FcmToken)) errors["fcmToken"] = ["FCM token must contain 1-4096 non-whitespace printable characters."];
        if (InvalidMetadata(request.DeviceModel, 255)) errors["deviceModel"] = ["Device model must contain at most 255 characters and no control characters."];
        if (InvalidMetadata(request.OsVersion, 100)) errors["osVersion"] = ["OS version must contain at most 100 characters and no control characters."];
        if (InvalidMetadata(request.AppVersion, 100)) errors["appVersion"] = ["App version must contain at most 100 characters and no control characters."];
        return errors;
    }

    internal static bool TryValidInstallationKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) return false;
        try
        {
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
            return bytes.Length == 32 && string.Equals(Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), value, StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
    }

    internal static string HashInstallationKey(string value)
    {
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool InvalidFcmToken(string? value) => value is not null &&
        (value.Length == 0 || value.Length > 4096 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl));
    private static bool InvalidMetadata(string? value, int max) => value is not null &&
        (value.Length == 0 || value.Length > max || value.Any(char.IsControl));
}

