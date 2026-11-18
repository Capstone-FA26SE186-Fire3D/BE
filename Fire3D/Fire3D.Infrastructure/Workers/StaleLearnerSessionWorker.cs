using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fire3D.Infrastructure.Workers;

/// <summary>
/// Closes learner sessions a phone abandoned without completing (#207/#208): Launching or Running, silent for 30 minutes and past
/// their continuation or silent for 24 hours. learner_session_close_stale completes each one as Abandoned at its acknowledged
/// sequence, so history shows a result and the AI block no longer sees it.
/// </summary>
public sealed class StaleLearnerSessionWorker(IServiceScopeFactory scopes, IOptions<LearnerSessionOptions> options,
    ILogger<StaleLearnerSessionWorker> logger) : BackgroundService
{
    internal const int BatchSize = 100;
    internal const int MaxBatchesPerRun = 10;
    internal TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.StaleCloseEnabled)
        {
            logger.LogInformation("Stale learner session close is disabled.");
            return;
        }
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                logger.LogError("Stale learner session close blocked. SQLSTATE={SqlState}; retry next interval.", exception.SqlState);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Stale learner session close failed.");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
            var closed = await db.Database.SqlQueryRaw<int>(
                "SELECT public.learner_session_close_stale(100) AS \"Value\"").SingleAsync(ct);
            total += closed;
            if (closed < BatchSize) break;
        }
        if (total > 0) logger.LogInformation("Closed {Count} stale learner sessions as Abandoned.", total);
        return total;
    }
}
