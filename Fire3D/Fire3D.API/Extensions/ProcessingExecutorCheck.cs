using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fire3D.API.Extensions;

/// <summary>
/// BE#118: GET /health/worker (machine key only) checks the restricted ProcessingExecutor login the worker API runs on. Kept out of
/// /health and /health/ready, so a worker misconfiguration never marks the whole API unhealthy. Only codes are reported, never
/// connection details. A result is reused for 30 seconds so repeated calls never turn into repeated database logins (a wrong
/// password would otherwise hit the pooler's failed-authentication limit).
/// </summary>
public sealed class ProcessingExecutorCheck(IOptions<ProcessingWorkerOptions> options, IConfiguration configuration) : IHealthCheck
{
    public const string Tag = "worker";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, HealthCheckResult Result)> Recent = new();

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!options.Value.WorkerApiEnabled) return HealthCheckResult.Healthy("WORKER_API_DISABLED");
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            (configuration.GetConnectionString("ProcessingExecutor") ?? "") + "|" + options.Value.PackagePullEnabled + "|" + options.Value.GeometryPullEnabled)));
        if (Recent.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor) return cached.Result;
        var result = await ProbeAsync(ct);
        Recent[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString("ProcessingExecutor"));
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("""
                SELECT has_function_privilege(current_user,'processing_worker_gate(text,uuid,jsonb)','EXECUTE'),
                       has_function_privilege(current_user,'processing_package_lease_gate(jsonb)','EXECUTE'),
                       r.rolsuper OR r.rolbypassrls,
                       -- Absent before migration AddGeometryPull: reported as not executable instead of failing the probe.
                       COALESCE(has_function_privilege(current_user,to_regprocedure('processing_geometry_lease_gate(jsonb)')::oid,'EXECUTE'),false)
                FROM pg_roles r WHERE r.rolname=current_user
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return HealthCheckResult.Unhealthy("PROCESSING_EXECUTOR_FORBIDDEN");
            var (gate, lease, privileged, geometryLease) = (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
            if (!gate || options.Value.PackagePullEnabled && !lease || options.Value.GeometryPullEnabled && !geometryLease)
                return HealthCheckResult.Unhealthy("PROCESSING_EXECUTOR_FORBIDDEN");
            // Works, but defeats the separation: the worker login must not bypass row security.
            return privileged ? HealthCheckResult.Degraded("PROCESSING_EXECUTOR_TOO_PRIVILEGED") : HealthCheckResult.Healthy("OK");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(ProcessingExecutorUnavailableException.CodeFor(ex) ?? "PROCESSING_EXECUTOR_UNREACHABLE",
                data: new Dictionary<string, object> { ["error"] = ex.GetType().Name });
        }
    }

    public static HealthCheckOptions Options => new()
    {
        Predicate = check => check.Tags.Contains(Tag),
        ResponseWriter = DatabaseReadinessCheck.WriteResponse,
        ResultStatusCodes = { [HealthStatus.Healthy] = 200, [HealthStatus.Degraded] = 503, [HealthStatus.Unhealthy] = 503 }
    };
}

/// <summary>A worker request reached a ProcessingExecutor login that cannot run the worker gates: 503 with a configuration code.</summary>
public sealed class ProcessingExecutorExceptionHandler(IProblemDetailsService problems, ILogger<ProcessingExecutorExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not ProcessingExecutorUnavailableException ex) return false;
        log.LogError("Processing executor misconfigured: {Code} ({SqlState})", ex.Code, (ex.InnerException as PostgresException)?.SqlState);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new()
            {
                Status = 503, Title = "The worker database login is not configured correctly. See GET /health/worker.",
                Extensions = { ["code"] = ex.Code }
            }
        });
        return true;
    }
}
