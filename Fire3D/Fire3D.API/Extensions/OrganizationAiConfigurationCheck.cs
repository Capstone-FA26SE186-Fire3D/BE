using Fire3D.Application.Ai;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Fire3D.API.Extensions;

/// <summary>
/// Reports AI settings that leave Organization AI off although it is enabled (#176). Degraded, not Unhealthy: readiness still
/// answers 200 so the host keeps serving login, Learn and payments while AI submits return 503 AI_PROVIDER_UNAVAILABLE.
/// </summary>
public sealed class OrganizationAiConfigurationCheck(IOptions<OrganizationAiOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var o = options.Value;
        // Only the reason is reported; the base URL and key never leave the process.
        return Task.FromResult(o.Problem is { } problem
            ? HealthCheckResult.Degraded("AI_CONFIGURATION_INVALID", data: new Dictionary<string, object> { ["problem"] = problem })
            : HealthCheckResult.Healthy(o.Configured ? "OK" : "AI_DISABLED"));
    }
}
