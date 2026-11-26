using System.Net;
using Fire3D.Application.Authentication;
using Fire3D.Application.Email;
using Fire3D.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Workers;

/// <summary>BE#235: sends the first sign-in code of an account created by PlatformAdmin from the durable PostgreSQL queue.</summary>
public sealed class FirstLoginEmailWorker(IServiceScopeFactory scopes, IOptions<AuthEmailOptions> options,
    ILogger<FirstLoginEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IFirstLoginDeliveryQueue>();
                var job = await queue.ClaimAsync(stoppingToken);
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); continue; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    var code = WebUtility.HtmlEncode(job.Code);
                    var html = $"""
                        <!doctype html><html lang="vi"><body style="font-family:Arial,sans-serif;color:#222">
                        <h1 style="color:#d32f2f">FET3D</h1>
                        <h2>Xác minh email cho lần đăng nhập đầu</h2>
                        <p>Tài khoản Chuyên viên PCCC của bạn vừa được đăng nhập lần đầu. Mã xác minh là:</p>
                        <p style="font-size:32px;font-weight:bold;letter-spacing:8px">{code}</p>
                        <p>Mã có hiệu lực trong 10 phút. Không chia sẻ mã này cho bất kỳ ai. Nếu bạn không đăng nhập, hãy báo quản trị viên.</p>
                        </body></html>
                        """;
                    await scope.ServiceProvider.GetRequiredService<IEmailService>().SendAsync(
                        job.Email, "[FET3D] Mã xác minh đăng nhập lần đầu", html, timeout.Token);
                    await queue.CompleteAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    var delivery = exception as EmailDeliveryException;
                    await queue.FailAsync(job, delivery?.IsPermanent == true, stoppingToken);
                    logger.LogWarning("First sign-in code email job {JobId} failed on attempt {Attempt}. ErrorType={ErrorType}, ProviderStatusCode={ProviderStatusCode}, ProviderRequestId={ProviderRequestId}.",
                        job.Id, job.Attempt, exception.GetType().Name, (int?)delivery?.StatusCode, delivery?.ProviderRequestId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("First sign-in code email queue polling failed ({ErrorType}).", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
