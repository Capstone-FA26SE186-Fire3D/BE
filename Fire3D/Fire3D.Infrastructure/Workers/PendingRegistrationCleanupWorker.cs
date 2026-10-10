using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fire3D.Infrastructure.Workers;

/// <summary>Removes expired, never-verified self registrations without cascading into business data.</summary>
public sealed class PendingRegistrationCleanupWorker(IServiceScopeFactory scopes,
    ILogger<PendingRegistrationCleanupWorker> logger) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeleteBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                logger.LogError("Pending registration cleanup blocked. SQLSTATE={SqlState}; retry in 60 seconds.", exception.SqlState);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Expired registration cleanup failed.");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> DeleteBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
        var removed = await db.Database.SqlQueryRaw<int>(
            "SELECT public.cleanup_pending_registrations(100) AS \"Value\"").SingleAsync(ct);
        if (removed > 0) logger.LogInformation("Deleted {Count} expired pending registrations.", removed);
        return removed;
    }
}