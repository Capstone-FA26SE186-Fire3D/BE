using Fire3D.Application.Authentication.Avatar;
using Fire3D.Application.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fire3D.Infrastructure.Workers;

/// <summary>Retries private-object cleanup after avatar state has committed.</summary>
public sealed class AvatarCleanupWorker(IServiceScopeFactory scopes, ILogger<AvatarCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<IAvatarCleanupStore>();
                var job = await cleanup.ClaimAsync(stoppingToken);
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); continue; }
                try
                {
                    if (!await cleanup.IsReferencedAsync(job.ObjectKey, stoppingToken))
                        await scope.ServiceProvider.GetRequiredService<IStorageService>().DeleteObjectAsync(job.ObjectKey, stoppingToken);
                    await cleanup.CompleteAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Avatar cleanup failed for {ObjectKey}.", job.ObjectKey);
                    await cleanup.FailAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Avatar cleanup worker failed.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
