using Fire3D.Infrastructure.Persistence;
using Fire3D.Infrastructure;
using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
                
                var claimSql = @"
                    UPDATE public.integration_outbox_events
                    SET status = 'Leased', error_message = NULL
                    WHERE event_id = (
                        SELECT event_id FROM public.integration_outbox_events
                        WHERE aggregate_type = 'PasswordReset' 
                          AND (status = 'Pending' OR (status = 'Failed' AND (error_message IS NULL OR error_message NOT LIKE '%Permanent%')))
                        ORDER BY created_at
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                    )
                    RETURNING event_id, payload;
                ";
                
                Guid? eventId = null;
                string payload = null;
                
                using (var command = db.Database.GetDbConnection().CreateCommand())
                {
                    command.CommandText = claimSql.Replace('"', '"');
                    await db.Database.OpenConnectionAsync(stoppingToken);
                    using var reader = await command.ExecuteReaderAsync(stoppingToken);
                    if (await reader.ReadAsync(stoppingToken))
                    {
                        eventId = reader.GetGuid(0);
                        payload = reader.GetString(1);
                    }
                }

                if (eventId != null && !string.IsNullOrEmpty(payload))
                {
                    try 
                    {
                        var email = JsonNode.Parse(payload)?["email"]?.GetValue<string>();
                        
                        if (!string.IsNullOrEmpty(email))
                        {
                            var authStore = scope.ServiceProvider.GetRequiredService<IAuthStore>();
                            var user = await authStore.FindUserByEmailAsync(email, stoppingToken);

                            if (user != null && !string.IsNullOrEmpty(user.FirebaseUid))
                            {
                                var provider = scope.ServiceProvider.GetRequiredService<IPasswordResetProvider>();
                                var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

                                var link = await provider.GenerateResetLinkAsync(email, stoppingToken);
                                var htmlBody = "<p>Please click the button below to reset your password:</p><a href='" + link + "'>Reset Password</a><p>Ignore if you did not request this.</p>";
                                
                                await emailService.SendAsync(email, "Reset Password - Fire3D", htmlBody, stoppingToken);
                            }
                        }

                        var completeSql = "UPDATE public.integration_outbox_events SET status = 'Published' WHERE event_id = @id";
                        using var completeCmd = db.Database.GetDbConnection().CreateCommand();
                        completeCmd.CommandText = completeSql;
                        var p1 = completeCmd.CreateParameter();
                        p1.ParameterName = "@id";
                        p1.Value = eventId;
                        completeCmd.Parameters.Add(p1);
                        await completeCmd.ExecuteNonQueryAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        var failSql = "UPDATE public.integration_outbox_events SET status = 'Failed', error_message = @err WHERE event_id = @id";
                        using var failCmd = db.Database.GetDbConnection().CreateCommand();
                        failCmd.CommandText = failSql;
                        var p1 = failCmd.CreateParameter(); p1.ParameterName = "@id"; p1.Value = eventId;
                        var p2 = failCmd.CreateParameter(); p2.ParameterName = "@err"; p2.Value = ex.Message;
                        failCmd.Parameters.Add(p1); failCmd.Parameters.Add(p2);
                        await failCmd.ExecuteNonQueryAsync(stoppingToken);
                        logger.LogError(ex, "Error processing password reset outbox event {EventId}", eventId);
                    }
                }
                else
                {
                    await Task.Delay(5000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling password reset outbox events.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}


