using Fire3D.Application.Ai;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Fire3D.API.Extensions;

/// <summary>
/// Lists AI settings that leave Organization AI off although it is configured (#176). Always Healthy: AI is optional, so the
/// host keeps serving login, Learn and payments (AI submits return 503 AI_PROVIDER_UNAVAILABLE); the code AI_CONFIGURATION_INVALID
/// under checks.organizationAi is what the deploy smoke test warns on. Degraded/Unhealthy stay 503 for required dependencies.
/// </summary>
public sealed class OrganizationAiConfigurationCheck(IOptions<OrganizationAiOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var o = options.Value;
        // Only the reason is reported; the base URL and key never leave the process.
        return Task.FromResult(o.Problem is { } problem
            ? HealthCheckResult.Healthy("AI_CONFIGURATION_INVALID", new Dictionary<string, object> { ["problem"] = problem })
            : HealthCheckResult.Healthy(o.Configured ? "OK" : "AI_DISABLED"));
    }
}
