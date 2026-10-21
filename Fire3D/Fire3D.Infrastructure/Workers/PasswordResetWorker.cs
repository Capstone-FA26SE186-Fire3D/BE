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
    internal const string Subject = "[FET3D] Đặt lại mật khẩu";

    // Vietnamese like the registration OTP email; the link lifetime matches LocalPasswordReset (30 minutes).
    internal static string Html(string link) => $"""
        <!doctype html><html lang="vi"><body style="font-family:Arial,sans-serif;color:#222">
        <h1 style="color:#d32f2f">FET3D</h1>
        <h2>Đặt lại mật khẩu</h2>
        <p>Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản FET3D của bạn.</p>
        <p><a href="{WebUtility.HtmlEncode(link)}" style="display:inline-block;background:#d32f2f;color:#fff;padding:12px 24px;border-radius:6px;text-decoration:none;font-weight:bold">Đặt lại mật khẩu</a></p>
        <p>Liên kết có hiệu lực trong 30 phút và chỉ dùng được một lần.</p>
        <p>Nếu bạn không yêu cầu đặt lại mật khẩu, hãy bỏ qua email này; mật khẩu hiện tại vẫn giữ nguyên.</p>
        </body></html>
        """;

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
                        await scope.ServiceProvider.GetRequiredService<IEmailService>()
                            .SendAsync(job.Email,Subject,Html(link),timeout.Token);
                    }
                    await queue.CompleteAsync(job,stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    var delivery = ex as Fire3D.Infrastructure.Email.EmailDeliveryException;
                    await queue.FailAsync(job, ex is PasswordResetException { Permanent:true } || delivery?.IsPermanent == true, stoppingToken);
                    // No provider response body, link, code or recipient in logs.
                    logger.LogWarning("Password reset email job {JobId} failed on attempt {Attempt}. ErrorType={ErrorType}, ProviderStatusCode={ProviderStatusCode}, ProviderRequestId={ProviderRequestId}.",
                        job.Id, job.Attempt, ex.GetType().Name, (int?)delivery?.StatusCode, delivery?.ProviderRequestId);
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
