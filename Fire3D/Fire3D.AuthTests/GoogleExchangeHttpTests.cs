using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.API.Controllers;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Commands.FirebaseLogin;
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

public sealed class GoogleExchangeHttpTests
{
    [Theory]
    [InlineData(GoogleIdentityFailure.InvalidToken, 401, "INVALID_FIREBASE_TOKEN")]
    [InlineData(GoogleIdentityFailure.ProviderUnavailable, 503, "GOOGLE_PROVIDER_UNAVAILABLE")]
    public async Task Anonymous_exchange_preserves_error_status_and_safe_problem_details(
        GoogleIdentityFailure failure, int expectedStatus, string expectedCode)
    {
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => throw new GoogleIdentityException(failure));
        var store = ResetProxy.For<IAuthStore>((_, _) => throw new Exception("Must not access DB"));
        var tokens = ResetProxy.For<ITokenService>((_, _) => throw new Exception("Must not issue tokens"));
        var handler = new ExchangeFirebaseTokenCommandHandler(store, tokens, provider, TimeProvider.System, GoogleOnboardingTestDoubles.Proofs());
        var sender = ResetProxy.For<ISender>((_, args) => handler.Handle((ExchangeFirebaseTokenCommand)args[0]!, (CancellationToken)args[1]!));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test", ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[64])
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging(); services.AddRouting(); services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            services.AddAccountAuthentication(config); services.RemoveAll<IHostedService>(); services.AddSingleton(sender);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
        using var client = host.GetTestClient();
        var response = await client.PostAsJsonAsync("/api/auth/login-firebase", "sensitive-id-token");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(expectedCode, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("status").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
        Assert.DoesNotContain("sensitive-id-token", body);
    }
}
