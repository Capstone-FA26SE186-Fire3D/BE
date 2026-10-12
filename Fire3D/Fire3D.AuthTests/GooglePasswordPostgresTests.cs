using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.API.Controllers;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GooglePasswordPostgresTests
{
    private const string Uid = "owner-google";
    private const string NewPassword = "first-local-password";

    // Owner becomes Google-only; Other keeps its local password.
    private static async Task Prepare(BillingDatabase db)
    {
        await GoogleLinkPostgresTests.Prepare(db);
        await db.Sql($"UPDATE users SET password_hash=NULL,firebase_uid='{Uid}' WHERE id='{BillingDatabase.Owner}'");
    }
    private static IIdentityProvider Provider(TimeSpan? age = null, string uid = Uid, Func<Task>? during = null, bool noAuthTime = false) =>
        ResetProxy.For<IIdentityProvider>((_, _) => Verify(age ?? TimeSpan.FromMinutes(1), uid, during, noAuthTime));
    private static async Task<VerifiedIdentity> Verify(TimeSpan age, string uid, Func<Task>? during, bool noAuthTime)
    {
        if (during is not null) await during();
        return new(uid, "owner-google@example.test", null, noAuthTime ? null : DateTimeOffset.UtcNow - age);
    }
    private static Task<AuthResult<bool>> Set(Fire3DDbContext context, IIdentityProvider? provider = null, Guid? actor = null,
        string password = NewPassword) =>
        new GooglePasswordService(context, new AuthStore(context), GoogleLinkPostgresTests.Passwords, provider ?? Provider(), TimeProvider.System)
            .SetAsync(actor ?? BillingDatabase.Owner, actor ?? BillingDatabase.Owner, new("fresh-google-id-token", password), default);

    [BillingPostgresFact]
    public async Task Restricted_runtime_sets_first_password_revokes_sessions_and_enables_password_sign_in()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        await using var context = db.Context();
        await context.Database.OpenConnectionAsync(); await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
        var result = await Set(context); Assert.True(result.IsSuccess, result.Error?.Code);

        await using var check = db.Context();
        var user = await new AuthStore(check).FindUserAsync(BillingDatabase.Owner, default);
        Assert.True(GoogleLinkPostgresTests.Passwords.Verify(user!, NewPassword, out _));
        Assert.Equal(Uid, user!.FirebaseUid);
        var account = GoogleAuthRules.ToAccount(user);
        Assert.True(account.HasPassword); Assert.True(account.GoogleLinked);
        Assert.Equal(0L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Owner}' AND revoked_at IS NULL"));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Other}' AND revoked_at IS NULL"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM local_password_reset_tokens WHERE used_at IS NOT NULL"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs WHERE action='Update'"));

        // The revoked session cannot call again; Google is not contacted.
        await using var again = db.Context();
        var never = ResetProxy.For<IIdentityProvider>((_, _) => throw new Exception("Must not contact provider"));
        Assert.Equal("UNAUTHORIZED", (await Set(again, never)).Error?.Code);
    }

    [BillingPostgresFact]
    public async Task Stale_or_foreign_google_sign_in_and_ineligible_accounts_change_nothing()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        Assert.Equal("RECENT_LOGIN_REQUIRED", (await Set(context, Provider(TimeSpan.FromMinutes(6)))).Error?.Code);
        Assert.Equal("RECENT_LOGIN_REQUIRED", (await Set(context, Provider(noAuthTime: true))).Error?.Code);
        Assert.Equal("INVALID_FIREBASE_TOKEN", (await Set(context, Provider(uid: "someone-else"))).Error?.Code);
        var invalid = await Set(context, password: "     ");
        Assert.Equal("VALIDATION_ERROR", invalid.Error?.Code); Assert.True(invalid.Error!.Errors!.ContainsKey("newPassword"));

        var never = ResetProxy.For<IIdentityProvider>((_, _) => throw new Exception("Must not contact provider"));
        Assert.Equal("PASSWORD_ALREADY_SET", (await Set(context, never, BillingDatabase.Other)).Error?.Code);
        await db.Sql($"UPDATE users SET firebase_uid=NULL WHERE id='{BillingDatabase.Owner}'");
        Assert.Equal("GOOGLE_LINK_REQUIRED", (await Set(context, never)).Error?.Code);

        Assert.Null(await db.Scalar($"SELECT password_hash FROM users WHERE id='{BillingDatabase.Owner}'") as string);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(0L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Owner}' AND revoked_at IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Changes_during_the_provider_wait_are_rechecked_under_the_lock()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        var revoked = Provider(during: () => db.Sql($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{BillingDatabase.Owner}'"));
        Assert.Equal(401, (await Set(context, revoked)).Error?.Status);
        await db.Sql($"UPDATE auth_refresh_tokens SET revoked_at=NULL WHERE user_id='{BillingDatabase.Owner}'");
        var unlinked = Provider(during: () => db.Sql($"UPDATE users SET firebase_uid='replaced-google' WHERE id='{BillingDatabase.Owner}'"));
        Assert.Equal("ACCOUNT_CHANGED", (await Set(context, unlinked)).Error?.Code);
        Assert.Null(await db.Scalar($"SELECT password_hash FROM users WHERE id='{BillingDatabase.Owner}'") as string);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Concurrent_requests_set_the_password_once()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        async Task<AuthResult<bool>> Attempt(string password)
        {
            await using var context = db.Context(); return await Set(context, password: password);
        }
        var results = await Task.WhenAll(Attempt("first-password-a"), Attempt("first-password-b"));
        Assert.Single(results, x => x.IsSuccess);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs WHERE action='Update'"));
        Assert.NotNull(await db.Scalar($"SELECT password_hash FROM users WHERE id='{BillingDatabase.Owner}'") as string);
    }

    [BillingPostgresFact]
    public async Task Http_set_password_returns_204_and_the_old_bearer_is_rejected()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = db.Connection,
            ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test", ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[64])
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging(); services.AddRouting(); services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            services.AddDatabase(config); services.AddAccountAuthentication(config); services.RemoveAll<IHostedService>();
            services.AddMediatR(options => options.RegisterServicesFromAssembly(typeof(SetPasswordRequest).Assembly));
            services.AddSingleton(Provider());
        }).Configure(app =>
        {
            app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
        await using var context = db.Context();
        var user = (await new AuthStore(context).FindUserAsync(BillingDatabase.Owner, default))!;
        using var scope = host.Services.CreateScope();
        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().CreateAccessToken(user, BillingDatabase.Owner, DateTime.UtcNow);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        var invalid = await client.PostAsJsonAsync("/api/me/password", new { idToken = "", newPassword = "x" });
        Assert.Equal(400, (int)invalid.StatusCode);
        using (var errors = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
        {
            Assert.Equal("VALIDATION_ERROR", errors.RootElement.GetProperty("code").GetString());
            Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("idToken", out _));
            Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("newPassword", out _));
        }
        var response = await client.PostAsJsonAsync("/api/me/password", new { idToken = "fresh-google", newPassword = NewPassword });
        Assert.Equal(204, (int)response.StatusCode);
        var oldSession = await client.PostAsJsonAsync("/api/me/password", new { idToken = "fresh-google", newPassword = NewPassword });
        Assert.Equal(401, (int)oldSession.StatusCode);
    }
}
