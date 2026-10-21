using Fire3D.Application.Ifc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fire3D.API.Extensions;

/// <summary>Read-only dependency checks for the enabled Redis processing loops.</summary>
public sealed class ProcessingReadinessCheck(IOptions<ProcessingWorkerOptions> options, IProcessingReadinessProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var worker = options.Value;
        if (worker.Transport != "RedisStreams" || (!worker.DispatcherEnabled && !worker.ConsumerEnabled))
            return HealthCheckResult.Healthy("PROCESSING_DISABLED");

        var dependency = "PROCESSING_REDIS";
        try
        {
            if (!await probe.RedisConnectedAsync(ct))
                return HealthCheckResult.Unhealthy(dependency + "_UNAVAILABLE");

            if (worker.DispatcherEnabled)
            {
                dependency = "PROCESSING_DISPATCHER";
                if (!await probe.CanExecuteGateAsync("DispatcherExecutor", "processing_stream_dispatch_gate(text,jsonb)", ct))
                    return HealthCheckResult.Unhealthy(dependency + "_FORBIDDEN");
            }

            if (worker.ConsumerEnabled)
            {
                dependency = "PROCESSING_CONSUMER";
                if (!await probe.CanExecuteGateAsync("DefaultConnection", "processing_stream_consumer_gate(text,jsonb)", ct))
                    return HealthCheckResult.Unhealthy(dependency + "_FORBIDDEN");
            }

            // Recovery runs whenever either publisher or consumer is enabled.
            dependency = "PROCESSING_RECOVERY";
            if (!await probe.CanExecuteGateAsync("DefaultConnection", "processing_stream_recovery_gate(text,jsonb)", ct))
                return HealthCheckResult.Unhealthy(dependency + "_FORBIDDEN");

            return HealthCheckResult.Healthy("OK");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Do not attach exceptions: providers can include connection strings or server details.
            return HealthCheckResult.Unhealthy(dependency + "_UNAVAILABLE");
        }
    }
}

public interface IProcessingReadinessProbe
{
    Task<bool> RedisConnectedAsync(CancellationToken ct);
    Task<bool> CanExecuteGateAsync(string connectionName, string gate, CancellationToken ct);
}

public sealed class ProcessingReadinessProbe(IProcessingStream stream, IConfiguration configuration) : IProcessingReadinessProbe
{
    public Task<bool> RedisConnectedAsync(CancellationToken ct) => stream.ConnectedAsync(ct);

    public async Task<bool> CanExecuteGateAsync(string connectionName, string gate, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString(connectionName));
        await connection.OpenAsync(ct);
        // Inspect the same login/search path used by ProcessingStreamStore. Never execute a gate:
        // even Claim, RecoverAttempt or a fake request can mutate authoritative processing state.
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(has_function_privilege(current_user, to_regprocedure(@gate)::oid, 'EXECUTE'), false)
                   AND NOT r.rolsuper AND NOT r.rolbypassrls
            FROM pg_roles r WHERE r.rolname = current_user
            """, connection);
        command.Parameters.AddWithValue("gate", gate);
        return await command.ExecuteScalarAsync(ct) is true;
    }
}
