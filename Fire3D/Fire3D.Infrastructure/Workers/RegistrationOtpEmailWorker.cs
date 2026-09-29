using System.Net;
using Fire3D.Application.Authentication;
using Fire3D.Application.Email;
using Fire3D.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Workers;

/// <summary>Sends pre-registration OTPs from the durable PostgreSQL queue; it does not create identities.</summary>
public sealed class RegistrationOtpEmailWorker(IServiceScopeFactory scopes, IOptions<AuthEmailOptions> options,
    ILogger<RegistrationOtpEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IRegistrationOtpDeliveryQueue>();
                var job = await queue.ClaimAsync(stoppingToken);
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); continue; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    var code = WebUtility.HtmlEncode(job.Otp);
                    var html = $"""
                        <!doctype html><html lang="vi"><body style="font-family:Arial,sans-serif;color:#222">
                        <h1 style="color:#d32f2f">FET3D</h1>
                        <h2>Xác minh email trước khi đăng ký</h2>
                        <p>Mã xác minh của bạn là:</p>
                        <p style="font-size:32px;font-weight:bold;letter-spacing:8px">{code}</p>
                        <p>Mã có hiệu lực trong 10 phút. Không chia sẻ mã này cho bất kỳ ai.</p>
                        </body></html>
                        """;
                    await scope.ServiceProvider.GetRequiredService<IEmailService>().SendAsync(
                        job.Email, "[FET3D] Mã xác minh đăng ký", html, timeout.Token);
                    await queue.CompleteAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    var delivery = exception as EmailDeliveryException;
                    await queue.FailAsync(job, delivery?.IsPermanent == true, stoppingToken);
                    logger.LogWarning("Registration OTP email job {JobId} failed on attempt {Attempt}. ErrorType={ErrorType}, ProviderStatusCode={ProviderStatusCode}, ProviderRequestId={ProviderRequestId}.",
                        job.Id, job.Attempt, exception.GetType().Name, (int?)delivery?.StatusCode, delivery?.ProviderRequestId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Registration OTP email queue polling failed ({ErrorType}).", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
