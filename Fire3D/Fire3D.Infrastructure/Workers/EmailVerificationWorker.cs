using System.Net;
using Fire3D.Application.Authentication;
using Fire3D.Application.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Workers;

public sealed class EmailVerificationWorker(IServiceScopeFactory scopes, IOptions<AuthEmailOptions> options,
    ILogger<EmailVerificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IEmailVerificationQueue>();
                var job = await queue.ClaimAsync(stoppingToken);
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); continue; }
                try
                {
                    var link = await queue.CreateLinkAsync(job, stoppingToken);
                    if (link is not null)
                        await scope.ServiceProvider.GetRequiredService<IEmailService>().SendAsync(job.Email, "Verify your Fire3D email",
                            "<p>Verify your Fire3D email address.</p><p><a href=\"" + WebUtility.HtmlEncode(link) + "\">Verify email</a></p>", stoppingToken);
                    await queue.CompleteAsync(job, stoppingToken);
                }
                catch (Exception exception)
                {
                    await queue.FailAsync(job, false, stoppingToken);
                    logger.LogWarning("Email verification job {JobId} failed on attempt {Attempt} ({ErrorType}).", job.Id, job.Attempt, exception.GetType().Name);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Email verification queue polling failed ({ErrorType}).", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
