using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.API.Controllers;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Commands.FirebaseLogin;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Authentication;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleOnboardingHttpTests
{
    [BillingPostgresFact]
    public async Task Anonymous_exchange_proof_completes_and_replays_without_bearer()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        await using var context = db.Context();
        var store = new AuthStore(context);
        var service = new GoogleOnboardingService(context, store, TimeProvider.System, GoogleOnboardingTestDoubles.Tokens());
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => Task.FromResult(new VerifiedIdentity("http-google", "http@example.test")));
        var tokens = GoogleOnboardingTestDoubles.Tokens();
        var handler = new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, service);
        var sender = ResetProxy.For<ISender>((_, args) => handler.Handle((ExchangeFirebaseTokenCommand)args[0]!, (CancellationToken)args[1]!));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test", ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[64]) }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging(); services.AddRouting();
            services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            services.AddAccountAuthentication(config); services.RemoveAll<IHostedService>();
            services.AddSingleton(sender); services.AddSingleton<IGoogleOnboardingService>(service);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
        using var client = host.GetTestClient();
        var exchange = await client.PostAsJsonAsync("/api/auth/login-firebase", "fake-firebase-id-token");
        Assert.Equal(200, (int)exchange.StatusCode);
        var proof = await exchange.Content.ReadFromJsonAsync<GoogleExchangeResponse>();
        Assert.Equal("OnboardingRequired", proof!.Status); Assert.NotNull(proof.ExpiresAt); Assert.Null(proof.Authentication);
        var bad = await client.PostAsJsonAsync("/api/auth/google/onboarding/complete", new { onboardingToken = proof.OnboardingToken, accountType = "PlatformAdmin" });
        Assert.Equal(400, (int)bad.StatusCode);
        using var error = JsonDocument.Parse(await bad.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", error.RootElement.GetProperty("code").GetString());
        Assert.True(error.RootElement.GetProperty("errors").TryGetProperty("accountType", out _));
        Assert.True(error.RootElement.TryGetProperty("traceId", out _));
        var request = new { onboardingToken = proof.OnboardingToken, accountType = "Trainee", username = "http_user" };
        var first = await client.PostAsJsonAsync("/api/auth/google/onboarding/complete", request);
        var replay = await client.PostAsJsonAsync("/api/auth/google/onboarding/complete", request);
        Assert.Equal(201, (int)first.StatusCode); Assert.Equal(409, (int)replay.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body); Assert.Contains("accessToken", body);
        Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
    }
}
