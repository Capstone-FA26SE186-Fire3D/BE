using System.Net;
using Fire3D.Application.Authentication;
using Fire3D.Application.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Fire3D.Infrastructure.Workers;

public sealed class PasswordResetWorker(IServiceScopeFactory scopes, IOptions<AuthEmailOptions> options,
    ILogger<PasswordResetWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IPasswordResetQueue>();
                var job = await queue.ClaimAsync(stoppingToken);
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); continue; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45)); // Less than the lease; expired work cannot acknowledge a new owner.
                try
                {
                    var accounts = scope.ServiceProvider.GetRequiredService<IAuthStore>();
                    var user = await accounts.FindUserByEmailAsync(job.Email,timeout.Token);
                    if (user is { IsActive:true, DeletedAt:null })
                    {
                        var provider = scope.ServiceProvider.GetRequiredService<ILocalPasswordReset>();
                        var link = await provider.CreateLinkAsync(job.Email,timeout.Token);
                        if (link is null) { await queue.CompleteAsync(job,stoppingToken); continue; }
                        var html = "<p>A password reset was requested for your Fire3D account.</p><p><a href=\"" +
                            WebUtility.HtmlEncode(link) + "\">Reset password</a></p><p>Ignore this email if you did not request it.</p>";
                        await scope.ServiceProvider.GetRequiredService<IEmailService>()
                            .SendAsync(job.Email,"Reset your Fire3D password",html,timeout.Token);
                    }
                    await queue.CompleteAsync(job,stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    await queue.FailAsync(job,ex is PasswordResetException { Permanent:true },stoppingToken);
                    // No provider response body, link, code or recipient in logs.
                    logger.LogWarning("Password reset email job {JobId} failed on attempt {Attempt} ({ErrorType}).",
                        job.Id,job.Attempt,ex.GetType().Name);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Password reset queue polling failed ({ErrorType}).",ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
