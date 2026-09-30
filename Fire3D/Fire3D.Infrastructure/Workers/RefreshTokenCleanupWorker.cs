using Fire3D.Application.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Workers;

public sealed class RefreshTokenCleanupWorker(
    IServiceScopeFactory scopes,
    IOptions<AuthTokenCleanupOptions> configured,
    ILogger<RefreshTokenCleanupWorker> logger) : BackgroundService
{
    private readonly AuthTokenCleanupOptions options = configured.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Refresh-token cleanup worker is disabled.");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Refresh-token cleanup run failed.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!options.Enabled) return 0;

        var totalDeleted = 0;
        for (var batch = 0; batch < options.MaxBatchesPerRun; batch++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var cleanup = scope.ServiceProvider.GetRequiredService<IRefreshTokenCleanupStore>();
            var deleted = await cleanup.DeleteExpiredFamiliesAsync(options.RetentionDays, options.BatchSize, ct);
            totalDeleted += deleted;
            if (deleted == 0) break;
        }

        if (totalDeleted > 0)
            logger.LogInformation("Deleted {Count} expired refresh tokens.", totalDeleted);
        return totalDeleted;
    }
}
