using Fire3D.Application.Ai;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Fire3D.API.Extensions;

/// <summary>
/// Lists AI settings that leave Organization AI off although it is configured (#176), and whether the AI service answers its own
/// /ready (#185). Always Healthy: AI is optional, so the host keeps serving login, Learn and payments while AI is down; the code
/// under checks.organizationAi (AI_CONFIGURATION_INVALID, AI_PROVIDER_UNREACHABLE, AI_PROVIDER_NOT_READY) is what the deploy
/// smoke test and operators watch. Degraded/Unhealthy stay 503 for required dependencies, which would take the API out of rotation.
/// </summary>
public sealed class OrganizationAiConfigurationCheck(IOptions<OrganizationAiOptions> options, IAiProviderProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var o = options.Value;
        // Only the reason is reported; the base URL and key never leave the process.
        if (o.Problem is { } problem) return HealthCheckResult.Healthy("AI_CONFIGURATION_INVALID", new Dictionary<string, object> { ["problem"] = problem });
        if (!o.Configured) return HealthCheckResult.Healthy("AI_DISABLED");
        var result = await probe.ProbeAsync(ct);
        return result.HttpStatus is { } status
            ? HealthCheckResult.Healthy(result.Code, new Dictionary<string, object> { ["providerStatus"] = status })
            : HealthCheckResult.Healthy(result.Code);
    }
}
