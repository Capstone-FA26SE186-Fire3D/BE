using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Users.Commands.RegisterDevice;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Auth_store_upgrades_legacy_installation_hashes_on_register_and_revoke()
    {
        var token = (await LoginAsync()).AccessToken;
        var (registered, revoked) = (Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        var (registerKey, revokeKey) = (NewInstallationKey(), NewInstallationKey());
        foreach (var (device, key) in new[] { (registered, registerKey), (revoked, revokeKey) })
            await ExecuteAsync($"""
                INSERT INTO device_installations(id,device_uuid,secret_hash,secret_hash_scheme,created_at,updated_at)
                VALUES (gen_random_uuid(),'{device}','{DeviceInstallationProof.Create(key).LegacyHash}','{DeviceInstallationProof.LegacyHashScheme}',now()-interval '1 day',now()-interval '1 day')
                """);

        Assert.Equal(HttpStatusCode.OK, await PutDevice(token, registered, registerKey, "legacy-token"));
        Assert.Equal(true, await ScalarAsync($"""
            SELECT secret_hash='{DeviceInstallationProof.Create(registerKey).CurrentHash}' AND secret_hash_scheme='{DeviceInstallationProof.CurrentHashScheme}'
                   AND updated_at>created_at FROM device_installations WHERE device_uuid='{registered}'
            """));
        Assert.Equal(HttpStatusCode.OK, await PutDevice(token, registered, registerKey, "legacy-token-2"));

        // Revoke accepts the legacy proof and persists the upgrade too; the key keeps working against the new hash.
        Assert.Equal(HttpStatusCode.NoContent, await DeleteDevice(token, revoked, revokeKey));
        Assert.Equal(true, await ScalarAsync($"""
            SELECT secret_hash='{DeviceInstallationProof.Create(revokeKey).CurrentHash}' AND secret_hash_scheme='{DeviceInstallationProof.CurrentHashScheme}'
                   AND updated_at>created_at FROM device_installations WHERE device_uuid='{revoked}'
            """));
        Assert.Equal(HttpStatusCode.NoContent, await DeleteDevice(token, revoked, revokeKey));
        Assert.Equal(HttpStatusCode.Forbidden, await DeleteDevice(token, revoked, registerKey));
    }

    [PostgresFact]
    public async Task Auth_store_revoke_accepts_unknown_devices_and_rejects_another_installation_key()
    {
        var token = (await LoginAsync()).AccessToken;
        Assert.Equal(HttpStatusCode.NoContent, await DeleteDevice(token, Guid.NewGuid().ToString("D"), NewInstallationKey()));
        var device = Guid.NewGuid().ToString("D");
        var key = NewInstallationKey();
        Assert.Equal(HttpStatusCode.OK, await PutDevice(token, device, key, "owned-token"));
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/devices/{device}");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("X-Installation-Key", NewInstallationKey());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("INSTALLATION_KEY_INVALID", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(true, await ScalarAsync($"SELECT notifications_enabled AND fcm_token='owned-token' FROM user_devices WHERE device_uuid='{device}'"));
    }

    [PostgresFact]
    public async Task Auth_store_device_registration_rejects_a_43_character_key_that_is_not_base64url()
    {
        var token = (await LoginAsync()).AccessToken;
        Assert.Equal(HttpStatusCode.BadRequest, await PutDevice(token, Guid.NewGuid().ToString("D"), new string('*', 43), "token"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM device_installations"));
    }

    [PostgresFact]
    public async Task Auth_store_cancelled_lock_wait_releases_the_user_transaction()
    {
        await using var holder = new NpgsqlConnection(testConnection);
        await holder.OpenAsync();
        await using var held = await holder.BeginTransactionAsync();
        await new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:auth:{adminId}', 0))", holder, held).ExecuteNonQueryAsync();

        using var scope = factory!.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuthStore>();
        var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.BeginUserTransactionAsync(adminId, timeout.Token));
        Assert.Null(db.Database.CurrentTransaction);

        await held.RollbackAsync();
        await using (var transaction = await store.BeginUserTransactionAsync(adminId, default)) await transaction.CommitAsync(default);
    }

    [PostgresFact]
    public async Task Administration_store_cancelled_lock_wait_releases_the_management_transaction()
    {
        await using var holder = new NpgsqlConnection(testConnection);
        await holder.OpenAsync();
        await using var held = await holder.BeginTransactionAsync();
        await new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('fire3d:identity-management', 0))", holder, held).ExecuteNonQueryAsync();

        using var scope = factory!.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdministrationStore>();
        var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.BeginManagementTransactionAsync(timeout.Token));
        Assert.Null(db.Database.CurrentTransaction);

        await held.RollbackAsync();
        await using (var transaction = await store.BeginManagementTransactionAsync(default)) await transaction.CommitAsync(default);
    }

    [PostgresFact]
    public async Task Auth_store_try_create_user_returns_false_for_an_existing_email()
    {
        using var scope = factory!.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuthStore>();
        var now = DateTime.UtcNow;
        Assert.False(await store.TryCreateUserAsync(new User
        {
            Id = Guid.NewGuid(), Email = "admin@example.test", Role = UserRole.PlatformAdmin, IsActive = true,
            PasswordHash = "fixture-hash", CreatedAt = now, UpdatedAt = now
        }, default));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM users WHERE email='admin@example.test'"));
    }

    [PostgresFact]
    public async Task Auth_login_validates_input_hides_unknown_accounts_and_reports_expired_registrations()
    {
        var blank = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", ""));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@example.test", password));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await ExecuteAsync("UPDATE users SET registration_expires_at=now()-interval '1 minute', email_verified_at=NULL WHERE email='admin@example.test'");
        var expired = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", password));
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.Equal("REGISTRATION_EXPIRED", (await expired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM auth_refresh_tokens"));
    }

    [PostgresFact]
    public async Task Auth_refresh_revokes_the_family_when_the_account_is_pending_email_verification()
    {
        var tokens = await LoginAsync();
        await ExecuteAsync("UPDATE users SET registration_expires_at=now()+interval '1 day', email_verified_at=NULL WHERE email='admin@example.test'");
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM auth_refresh_tokens WHERE revoked_at IS NOT NULL AND consumed_at IS NULL"));
    }

    [PostgresFact]
    public async Task Auth_change_password_rejects_a_wrong_current_password_and_an_unchanged_password()
    {
        var tokens = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        var hash = await ScalarAsync("SELECT password_hash FROM users WHERE email='admin@example.test'");
        foreach (var (current, next, code) in new[] { ("Wrong-Password-2026!", "New-Example-Password-2026!", "INVALID_CURRENT_PASSWORD"), (password, password, "PASSWORD_UNCHANGED") })
        {
            var response = await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = current, newPassword = next });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(hash, await ScalarAsync("SELECT password_hash FROM users WHERE email='admin@example.test'"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM auth_refresh_tokens WHERE revoked_at IS NOT NULL"));
    }

    [PostgresFact]
    public async Task Auth_bearer_signed_with_the_real_key_but_without_a_session_id_is_rejected()
    {
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Convert.FromBase64String(signingKey)), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        string Token(params System.Security.Claims.Claim[] claims) => new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityToken("Fire3D.Tests", "Fire3D.Tests.Client", claims, now.AddMinutes(-1), now.AddMinutes(5), credentials));
        foreach (var token in new[]
        {
            Token(new("sub", adminId.ToString()), new("role", "PlatformAdmin")),
            Token(new("sub", "not-a-guid"), new("sid", Guid.NewGuid().ToString()), new("role", "PlatformAdmin"))
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            request.Headers.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }
        // Control: the same key and a live session id are accepted, so the rejections above are about the claims.
        await LoginAsync();
        var family = (Guid)(await ScalarAsync("SELECT family_id FROM auth_refresh_tokens LIMIT 1"))!;
        using var valid = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        valid.Headers.Authorization = new("Bearer", Token(new("sub", adminId.ToString()), new("sid", family.ToString()), new("role", "PlatformAdmin")));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(valid)).StatusCode);
    }

    [PostgresFact]
    public async Task Auth_store_session_check_rejects_non_canonical_organization_claims_and_reset_invalidation_needs_a_transaction()
    {
        using var scope = factory!.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuthStore>();
        var organization = Guid.NewGuid();
        foreach (var claim in new[] { organization.ToString("N"), organization.ToString("D").ToUpperInvariant(), "not-a-guid" })
            Assert.False(await store.SessionIsValidAsync(adminId, adminId, "OrganizationUser", claim, DateTime.UtcNow, default));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.InvalidateUserResetTokensAsync(adminId, default));
        Assert.Contains("user transaction", error.Message);
    }
}
