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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    var link = await queue.CreateLinkAsync(job, timeout.Token);
                    if (link is not null)
                    {
                        var verifyUrl = WebUtility.HtmlEncode(link);
                        var html = $"""
                            <!DOCTYPE html>
                            <html lang="vi">
                            <head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
                            <body style="margin:0;padding:0;background:#f4f6f9;font-family:Arial,sans-serif;">
                              <table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f6f9;padding:40px 0;">
                                <tr><td align="center">
                                  <table width="560" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.08);">
                                    <!-- Header -->
                                    <tr><td style="background:#d32f2f;padding:28px 40px;text-align:center;">
                                      <h1 style="margin:0;color:#ffffff;font-size:26px;letter-spacing:1px;">🔥 Fire3D</h1>
                                      <p style="margin:4px 0 0;color:#ffcdd2;font-size:13px;">Nền tảng huấn luyện PCCC thực tế ảo</p>
                                    </td></tr>
                                    <!-- Body -->
                                    <tr><td style="padding:36px 40px;">
                                      <h2 style="margin:0 0 12px;color:#1a1a1a;font-size:20px;">Xác minh địa chỉ email của bạn</h2>
                                      <p style="margin:0 0 24px;color:#555;font-size:15px;line-height:1.6;">
                                        Cảm ơn bạn đã đăng ký tài khoản <strong>Fire3D</strong>.<br>
                                        Vui lòng bấm vào nút bên dưới để xác minh địa chỉ email và kích hoạt tài khoản của bạn.
                                      </p>
                                      <!-- Button -->
                                      <table cellpadding="0" cellspacing="0" style="margin:0 auto 28px;">
                                        <tr><td style="background:#d32f2f;border-radius:6px;text-align:center;">
                                          <a href="{verifyUrl}" style="display:inline-block;padding:14px 36px;color:#ffffff;font-size:15px;font-weight:bold;text-decoration:none;letter-spacing:0.5px;">
                                            ✅ Xác minh Email
                                          </a>
                                        </td></tr>
                                      </table>
                                      <p style="margin:0 0 8px;color:#888;font-size:13px;">Hoặc copy đường dẫn này vào trình duyệt:</p>
                                      <p style="margin:0 0 28px;word-break:break-all;font-size:12px;color:#aaa;">{verifyUrl}</p>
                                      <hr style="border:none;border-top:1px solid #eee;margin:0 0 20px;">
                                      <p style="margin:0;color:#aaa;font-size:12px;">
                                        Link xác minh có hiệu lực trong <strong>15 phút</strong>.<br>
                                        Nếu bạn không đăng ký tài khoản này, vui lòng bỏ qua email này.
                                      </p>
                                    </td></tr>
                                    <!-- Footer -->
                                    <tr><td style="background:#f9f9f9;padding:16px 40px;text-align:center;">
                                      <p style="margin:0;color:#bbb;font-size:12px;">© 2026 Fire3D · Hệ thống huấn luyện PCCC thực tế ảo</p>
                                    </td></tr>
                                  </table>
                                </td></tr>
                              </table>
                            </body>
                            </html>
                            """;
                        await scope.ServiceProvider.GetRequiredService<IEmailService>().SendAsync(
                            job.Email, "[Fire3D] Xác minh địa chỉ email của bạn", html, timeout.Token);
                    }
                    await queue.CompleteAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
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
