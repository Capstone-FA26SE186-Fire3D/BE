using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleOnboardingSessionHttpTests
{
    internal static WebApplicationFactory<Program> Factory(BillingDatabase db) =>
        new BillingApiTests.Factory(db).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.PostConfigure<AuthenticationOptions>(options =>
            { options.DefaultAuthenticateScheme = "Bearer"; options.DefaultChallengeScheme = "Bearer"; });
            services.RemoveAll<IIdentityProvider>();
            services.AddSingleton(ResetProxy.For<IIdentityProvider>((_, _) =>
                Task.FromResult(new VerifiedIdentity("http-session", "http-session@example.test", "Verified Name"))));
            services.RemoveAll<IStorageService>();
            services.AddSingleton(ResetProxy.For<IStorageService>((_, _) => throw new InvalidOperationException("No S3 in onboarding tests")));
        }));

    [BillingPostgresFact]
    public async Task Complete_returns_usable_bearer_refresh_and_lost_response_recovers_via_exchange()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        using var factory = Factory(db); using var client = factory.CreateClient();
        var exchange = await client.PostAsJsonAsync("/api/auth/login-firebase", "fake-google-token");
        using var proof = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync());
        var token = proof.RootElement.GetProperty("onboardingToken").GetString();
        var request = new { onboardingToken = token, accountType = "trainee", username = "http_session_user" };
        var completed = await client.PostAsJsonAsync("/api/auth/google/onboarding/complete", request);
        Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
        using var first = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        Assert.Equal("Authenticated", first.RootElement.GetProperty("status").GetString());
        var authentication = first.RootElement.GetProperty("authentication");
        var access = authentication.GetProperty("accessToken").GetString();
        var refresh = authentication.GetProperty("refreshToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        var rotation = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        var beforeReplay = await db.Scalar("SELECT count(*) FROM auth_refresh_tokens");
        var replay = await client.PostAsJsonAsync("/api/auth/google/onboarding/complete", request);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var replayError = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("ONBOARDING_ALREADY_COMPLETED", replayError.RootElement.GetProperty("code").GetString());
        Assert.Equal(beforeReplay, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
        var recovered = await client.PostAsJsonAsync("/api/auth/login-firebase", "fake-google-token");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        using var recovery = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", recovery.RootElement.GetProperty("authentication").GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        await db.Sql("UPDATE users SET is_active=false WHERE firebase_uid='http-session'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/login-firebase", "fake-google-token")).StatusCode);
        Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM users"));
    }

    [BillingPostgresFact]
    public async Task Invalid_json_fields_have_safe_field_errors_and_proof_quota_has_retry_after()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        using var factory = Factory(db); using var client = factory.CreateClient();
        foreach (var body in new[]
        {
            new { onboardingToken = "proof", accountType = "organization", dob = "2004-29-07" },
        })
        {
            var response = await client.PostAsJsonAsync("/api/auth/google/onboarding/complete", body);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("VALIDATION_ERROR", error.RootElement.GetProperty("code").GetString());
            Assert.True(error.RootElement.GetProperty("errors").TryGetProperty("dob", out _));
            Assert.True(error.RootElement.TryGetProperty("traceId", out _));
        }
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login-firebase", "fake-token")).StatusCode);
        var limited = await client.PostAsJsonAsync("/api/auth/login-firebase", "fake-token");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var values));
        Assert.InRange(int.Parse(Assert.Single(values)), 1, 900);
    }

    [BillingPostgresFact]
    public async Task Organization_completion_uses_canonical_type_and_cannot_override_verified_identity()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        using var factory = Factory(db); using var client = factory.CreateClient();
        using var exchange = JsonDocument.Parse(await (await client.PostAsJsonAsync("/api/auth/login-firebase", "fake-token")).Content.ReadAsStringAsync());
        var token = exchange.RootElement.GetProperty("onboarding").GetProperty("token").GetString();
        var rejected = await client.PostAsJsonAsync("/api/auth/google/onboarding/complete", new
        { onboardingToken = token, accountType = "organization", organizationId = BillingDatabase.Org, email = "attacker@example.test" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
        var body = new { onboardingToken = token, accountType = "organization", organizationName = "Google Org",
            organizationAddress = "1 Example Street", organizationPhoneNumber = "0706364866" };
        var completed = await client.PostAsJsonAsync("/api/auth/google/onboarding/complete", body);
        Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
        using var response = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        var auth = response.RootElement.GetProperty("authentication");
        Assert.Equal("OrganizationUser", auth.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal("http-session@example.test", auth.GetProperty("user").GetProperty("email").GetString());
        Assert.NotEqual(BillingDatabase.Org, auth.GetProperty("user").GetProperty("organizationId").GetGuid());
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        await db.Sql("UPDATE organizations SET is_active=false WHERE registration_owner_user_id IN (SELECT id FROM users WHERE firebase_uid='http-session')");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/login-firebase", "fake-token")).StatusCode);
    }
}
