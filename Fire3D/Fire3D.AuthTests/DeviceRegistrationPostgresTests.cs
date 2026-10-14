using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // AUTH-05: push bindings are owned by an installation secret and an account; previous unit tests only used stubs.
    [PostgresFact]
    public async Task Device_tokens_are_bound_per_installation_and_account_on_postgres()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        await CreateAccountAsync("device-one@example.test", UserRole.Trainee);
        await CreateAccountAsync("device-two@example.test", UserRole.Trainee);
        var phone = Guid.NewGuid().ToString("D"); var tablet = Guid.NewGuid().ToString("D");
        var phoneKey = NewInstallationKey(); var tabletKey = NewInstallationKey();
        var one = (await LoginAsync("device-one@example.test")).AccessToken;
        var two = (await LoginAsync("device-two@example.test")).AccessToken;

        Assert.Equal(HttpStatusCode.OK, await PutDevice(one, phone, phoneKey, "token-a"));
        Assert.Equal(HttpStatusCode.OK, await PutDevice(one, phone, phoneKey, "token-b"));
        Assert.Equal(2, await ScalarAsync($"SELECT fcm_token_generation FROM user_devices WHERE device_uuid='{phone}'"));
        Assert.Equal("token-b", await ScalarAsync($"SELECT fcm_token FROM user_devices WHERE device_uuid='{phone}'"));
        // One token cannot be active on two installations; a second device of the same account is fine.
        Assert.Equal(HttpStatusCode.Conflict, await PutDevice(one, tablet, tabletKey, "token-b"));
        Assert.Equal(HttpStatusCode.OK, await PutDevice(one, tablet, tabletKey, "token-c"));
        Assert.Equal(HttpStatusCode.Forbidden, await PutDevice(one, phone, tabletKey, "token-d"));

        // Another account cannot revoke a binding it does not own, even with the right installation key.
        Assert.Equal(HttpStatusCode.NoContent, await DeleteDevice(two, phone, phoneKey));
        Assert.Equal(true, await ScalarAsync($"SELECT notifications_enabled FROM user_devices WHERE device_uuid='{phone}'"));
        // The installation can move to the other account; the old binding is disabled in the same transaction.
        Assert.Equal(HttpStatusCode.OK, await PutDevice(two, phone, phoneKey, "token-e"));
        Assert.Equal(1L, await ScalarAsync($"""
            SELECT count(*) FROM user_devices d JOIN users u ON u.id=d.user_id
             WHERE d.device_uuid='{phone}' AND u.email='device-one@example.test'
               AND NOT d.notifications_enabled AND d.fcm_token IS NULL AND d.revoked_at IS NOT NULL
            """));

        client.DefaultRequestHeaders.Authorization = new("Bearer", one);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout-all", null)).StatusCode);
        Assert.Equal(0L, await ScalarAsync("""
            SELECT count(*) FROM user_devices d JOIN users u ON u.id=d.user_id
             WHERE u.email='device-one@example.test' AND d.notifications_enabled
            """));
        Assert.Equal(true, await ScalarAsync($"SELECT notifications_enabled FROM user_devices d JOIN users u ON u.id=d.user_id WHERE u.email='device-two@example.test'"));
    }

    private static string NewInstallationKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<HttpStatusCode> PutDevice(string accessToken, string deviceUuid, string installationKey, string fcmToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/auth/devices")
        {
            Content = JsonContent.Create(new { deviceUuid, fcmToken, deviceModel = "Pixel", osVersion = "15", appVersion = "1.0.0" })
        };
        request.Headers.Authorization = new("Bearer", accessToken);
        request.Headers.Add("X-Installation-Key", installationKey);
        return (await client.SendAsync(request)).StatusCode;
    }

    private async Task<HttpStatusCode> DeleteDevice(string accessToken, string deviceUuid, string installationKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/devices/{deviceUuid}");
        request.Headers.Authorization = new("Bearer", accessToken);
        request.Headers.Add("X-Installation-Key", installationKey);
        return (await client.SendAsync(request)).StatusCode;
    }
}
