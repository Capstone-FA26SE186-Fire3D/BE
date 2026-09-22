using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using Fire3D.Application.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fire3D.Infrastructure.Workers;

public class PasswordResetWorker(IServiceProvider serviceProvider, ILogger<PasswordResetWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
                
                var @event = await db.IntegrationOutboxEvents
                    .Where(e => e.AggregateType == ""PasswordReset"" && e.Status == ""Pending"")
                    .OrderBy(e => e.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);

                if (@event != null)
                {
                    @event.Status = ""Processing"";
                    await db.SaveChangesAsync(stoppingToken);

                    var email = JsonNode.Parse(@event.Payload)?[""email""]?.GetValue<string>();
                    
                    if (!string.IsNullOrEmpty(email))
                    {
                        var authStore = scope.ServiceProvider.GetRequiredService<IAuthStore>();
                        var user = await authStore.FindUserByEmailAsync(email, stoppingToken);

                        if (user != null && !string.IsNullOrEmpty(user.FirebaseUid))
                        {
                            var provider = scope.ServiceProvider.GetRequiredService<IPasswordResetProvider>();
                            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

                            var link = await provider.GenerateResetLinkAsync(email, stoppingToken);
                            var htmlBody = $""<p>Vui lòng nh?n vào nút du?i dây d? d?t l?i m?t kh?u:</p><a href='{link}'>Ð?t l?i m?t kh?u</a><p>B? qua n?u b?n không yêu c?u.</p>"";
                            
                            await emailService.SendAsync(email, ""Ð?t l?i m?t kh?u - Fire3D"", htmlBody, stoppingToken);
                        }
                    }

                    @event.Status = ""Completed"";
                    await db.SaveChangesAsync(stoppingToken);
                }
                else
                {
                    await Task.Delay(5000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ""Error processing password reset outbox events."");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
