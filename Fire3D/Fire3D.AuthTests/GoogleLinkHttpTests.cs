using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Infrastructure.Authentication;
using MediatR;
using Microsoft.Extensions.Options;
using Fire3D.API.Controllers;
using Fire3D.API.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleLinkHttpTests
{
    [BillingPostgresFact]
    public async Task Live_bearer_links_then_old_bearer_is_rejected_and_validation_is_field_specific()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleLinkPostgresTests.Prepare(db);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = db.Connection,
            ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test", ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[64])
        }).Build();
        var provider = GoogleLinkPostgresTests.Provider();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging(); services.AddRouting(); services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            services.AddDatabase(config); services.AddAccountAuthentication(config); services.RemoveAll<IHostedService>();
            services.AddMediatR(options => options.RegisterServicesFromAssembly(typeof(GoogleLinkRequest).Assembly));
            services.AddSingleton<IIdentityProvider>(provider);
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
        var invalid = await client.PostAsJsonAsync("/api/me/link-google", new { idToken = "", currentPassword = "" });
        Assert.Equal(400, (int)invalid.StatusCode);
        using var errors = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", errors.RootElement.GetProperty("code").GetString());
        Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("idToken", out _));
        Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("currentPassword", out _));
        Assert.True(errors.RootElement.TryGetProperty("traceId", out _));
        var response = await client.PostAsJsonAsync("/api/me/link-google", new { idToken = "fake-google", currentPassword = GoogleLinkPostgresTests.Password });
        Assert.Equal(200, (int)response.StatusCode); Assert.NotNull(response.Headers.ETag);
        var result = await response.Content.ReadFromJsonAsync<GoogleLinkResponse>();
        Assert.True(result!.RequiresLogin); Assert.False(result.AlreadyLinked);
        var oldSession = await client.PostAsJsonAsync("/api/me/link-google", new { idToken = "fake-google", currentPassword = GoogleLinkPostgresTests.Password });
        Assert.Equal(401, (int)oldSession.StatusCode);
    }

    [Fact]
    public async Task Explicit_link_requires_bearer_authentication()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test", ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[64]) }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging(); services.AddRouting(); services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            services.AddAccountAuthentication(config); services.RemoveAll<IHostedService>();
        }).Configure(app =>
        {
            app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync();
        using var client = host.GetTestClient();
        var response = await client.PostAsJsonAsync("/api/me/link-google", new { idToken = "fake", currentPassword = "password" });
        Assert.Equal(401, (int)response.StatusCode);
    }
}
