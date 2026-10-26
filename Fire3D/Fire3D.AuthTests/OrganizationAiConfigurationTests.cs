using System.Net;
using Fire3D.API.Extensions;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>#176: invalid AI settings turn AI off and are reported; they never stop the API from starting.</summary>
public sealed class OrganizationAiConfigurationTests
{
    private sealed class CapturingLog : ILogger<OrganizationAiWorker>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) { lock (Entries) Entries.Add((level, format(state, error))); }
    }

    private static Task<HealthCheckResult> Check(OrganizationAiOptions options) =>
        new OrganizationAiConfigurationCheck(Options.Create(options)).CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Enabled_ai_without_a_usable_adapter_is_off_and_degraded()
    {
        var disabled = await Check(new OrganizationAiOptions());
        Assert.Equal((HealthStatus.Healthy, "AI_DISABLED"), (disabled.Status, disabled.Description));
        var configured = await Check(new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.fixture.test/" });
        Assert.Equal((HealthStatus.Healthy, "OK"), (configured.Status, configured.Description));

        foreach (var broken in new[]
        {
            new OrganizationAiOptions { Enabled = true },
            new OrganizationAiOptions { Enabled = true, BaseUrl = "http://ai.fixture.test/?key=secret" },
            new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.fixture.test/", TimeoutSeconds = 60, LeaseSeconds = 60 }
        })
        {
            Assert.False(broken.Configured);
            var result = await Check(broken);
            Assert.Equal((HealthStatus.Degraded, "AI_CONFIGURATION_INVALID"), (result.Status, result.Description));
            Assert.DoesNotContain("secret", result.Data["problem"].ToString());
        }
        // Readiness keeps a degraded host in rotation; only Unhealthy dependencies answer 503.
        var codes = DatabaseReadinessCheck.Options.ResultStatusCodes;
        Assert.Equal((200, 503), (codes[HealthStatus.Degraded], codes[HealthStatus.Unhealthy]));
    }

    [Fact]
    public async Task Worker_logs_an_unusable_configuration_and_stays_idle()
    {
        var log = new CapturingLog();
        var worker = new OrganizationAiWorker(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OrganizationAiOptions { Enabled = true }), log);
        await worker.StartAsync(default);
        await worker.ExecuteTask!;
        Assert.Contains(log.Entries, x => x.Level == LogLevel.Error && x.Message.Contains("OrganizationAi:BaseUrl"));
    }

    [Fact]
    public async Task Api_starts_and_reports_ai_when_enabled_without_a_base_url()
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            web.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=fire3d_unused_host_test", ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[32]),
                ["Jwt:Issuer"] = "host-tests", ["Jwt:Audience"] = "host-tests", ["Mailgun:ApiKey"] = "test-key", ["Mailgun:Domain"] = "example.test",
                ["Mailgun:From"] = "noreply@example.test", ["AuthEmail:FrontendUrl"] = "https://example.test", ["AuthEmail:VerificationUrl"] = "https://example.test/verify",
                ["AuthEmail:WorkerEnabled"] = "false", ["PayOS:WorkerEnabled"] = "false", ["Redis:Enabled"] = "false", ["PlatformCache:WorkerEnabled"] = "false",
                // The production incident on 2026-10-26: AI enabled before BaseUrl was set.
                ["OrganizationAi:Enabled"] = "true"
            }));
            web.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/version")).StatusCode);
        Assert.False(host.Services.GetRequiredService<IOptions<OrganizationAiOptions>>().Value.Configured);
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(x => x.Name == "organizationAi");
        Assert.Equal("AI_CONFIGURATION_INVALID", report.Entries["organizationAi"].Description);
    }
}
