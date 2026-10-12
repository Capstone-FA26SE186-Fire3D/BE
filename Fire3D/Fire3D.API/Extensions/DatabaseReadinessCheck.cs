using System.Text.Json;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fire3D.API.Extensions;

/// <summary>
/// Readiness, not liveness: the database must be reachable and every EF migration in this build applied.
/// A deployment whose database lags the code answers 503 here instead of 500 on the first request that touches a new table.
/// </summary>
public sealed class DatabaseReadinessCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public const string Tag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
        try
        {
            if (!await db.Database.CanConnectAsync(ct))
                return HealthCheckResult.Unhealthy("DATABASE_UNREACHABLE");
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
            return pending.Length == 0
                ? HealthCheckResult.Healthy("OK")
                : HealthCheckResult.Unhealthy("MIGRATIONS_PENDING", data: new Dictionary<string, object> { ["pendingMigrations"] = pending });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the exception type is reported; connection details never leave the process.
            return HealthCheckResult.Unhealthy("DATABASE_CHECK_FAILED", data: new Dictionary<string, object> { ["error"] = ex.GetType().Name });
        }
    }

    public static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(x => x.Key, x => new
            {
                status = x.Value.Status.ToString(),
                code = x.Value.Description,
                data = x.Value.Data.Count == 0 ? null : x.Value.Data
            })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public static HealthCheckOptions Options => new()
    {
        Predicate = check => check.Tags.Contains(Tag),
        ResponseWriter = WriteResponse,
        ResultStatusCodes = { [HealthStatus.Healthy] = 200, [HealthStatus.Degraded] = 503, [HealthStatus.Unhealthy] = 503 }
    };
}
