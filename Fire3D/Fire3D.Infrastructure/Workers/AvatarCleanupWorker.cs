using Fire3D.Application.Authentication.Avatar;
using Fire3D.Application.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fire3D.Infrastructure.Workers;

/// <summary>Retries private-object cleanup after avatar state has committed.</summary>
public sealed class AvatarCleanupWorker(IServiceScopeFactory scopes, ILogger<AvatarCleanupWorker> logger) : BackgroundService
{
    /// <summary>Pause after every claimed job, whatever its outcome. A job that came straight back once kept this loop calling the
    /// gate about 20 times a second (46 million calls); with the pause a runaway job costs at most a few calls every 2 seconds.</summary>
    internal static readonly TimeSpan JobPause = TimeSpan.FromSeconds(2);

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
                    // The job remains durable until the active profile/intent stops referencing it.
                    if (await cleanup.IsReferencedAsync(job.ObjectKey, stoppingToken)) await cleanup.FailAsync(job, stoppingToken);
                    else if (await cleanup.RenewAsync(job, stoppingToken))
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(45));
                        try
                        {
                            await scope.ServiceProvider.GetRequiredService<IStorageService>().DeleteObjectAsync(job.ObjectKey, timeout.Token);
                        }
                        catch (Amazon.S3.AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound) { }
                        await cleanup.CompleteAsync(job, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning("Avatar cleanup failed. JobId={JobId}; Attempt={Attempt}; ErrorType={ErrorType}; SQLSTATE={SqlState}.",
                        job.Id, job.Attempt, exception.GetType().Name, (exception as Npgsql.PostgresException)?.SqlState);
                    await cleanup.FailAsync(job, stoppingToken);
                }
                await Task.Delay(JobPause, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Npgsql.PostgresException exception) when (exception.SqlState == Npgsql.PostgresErrorCodes.InsufficientPrivilege)
            {
                logger.LogError("Avatar cleanup blocked. SQLSTATE={SqlState}; retry in 60 seconds.", exception.SqlState);
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError("Avatar cleanup worker failed ({ErrorType}).", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
