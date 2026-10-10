using System.Net;
using Fire3D.Application.Email;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Billing;

public sealed class BillingReminderOptions
{
    public bool Enabled { get; set; }
    public int LeadDays { get; set; } = 5;
    public int PollSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>
/// Building service expiry reminders. Scheduling writes one notification per recipient and entitlement period with Web and Email
/// deliveries in the same transaction (the outbox). Dispatch rechecks renewal before sending; email is at-least-once.
/// </summary>
public sealed class BillingReminders(Fire3DDbContext db,IEmailService email,IOptions<BillingReminderOptions> options,ILogger<BillingReminders> logger)
{
    private sealed record PendingEmail(Guid DeliveryId,Guid EntitlementId,Guid BuildingId,string Recipient,string Title,string Body,DateTime ReferenceEndsAt);

    /// <summary>Creates reminders for paid entitlements ending within the lead window that have no committed renewal.</summary>
    public async Task<int> ScheduleAsync(DateTime now,CancellationToken ct)
    {
        var until=now.AddDays(options.Value.LeadDays);
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var created=await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH due AS (
             SELECT e.id,e.organization_id,e.building_id,e.ends_at,b.name FROM service_entitlements e
             JOIN buildings b ON b.id=e.building_id AND b.is_active AND b.deleted_at IS NULL
             JOIN organizations o ON o.id=e.organization_id AND o.is_active AND o.deleted_at IS NULL
             WHERE e.status='Active' AND e.payment_transaction_id IS NOT NULL AND e.ends_at>{now} AND e.ends_at<={until}
              AND NOT EXISTS(SELECT 1 FROM service_entitlements r WHERE r.building_id=e.building_id AND r.id<>e.id AND r.payment_transaction_id IS NOT NULL
               AND r.status IN('Active','Suspended') AND r.starts_at>=e.ends_at)
            ), inserted AS (
             INSERT INTO organization_notifications(id,organization_id,recipient_user_id,building_id,entitlement_id,notification_type,title,body,reference_ends_at,idempotency_key,created_at)
             SELECT gen_random_uuid(),d.organization_id,u.id,d.building_id,d.id,'BuildingServiceExpiring','Building service expires soon',
              'Service for '||d.name||' ends at '||to_char(d.ends_at AT TIME ZONE 'UTC','YYYY-MM-DD HH24:MI')||' UTC. Renew before it ends to keep training available.',
              d.ends_at,'expiring:'||d.id||':'||u.id,clock_timestamp()
             FROM due d JOIN users u ON u.organization_id=d.organization_id AND u.role='OrganizationUser' AND u.is_active AND u.deleted_at IS NULL
              AND (u.registration_expires_at IS NULL OR u.email_verified_at IS NOT NULL)
             ON CONFLICT (idempotency_key) DO NOTHING RETURNING id
            )
            INSERT INTO notification_deliveries(id,notification_id,channel,status,attempts,sent_at,created_at,updated_at)
            SELECT gen_random_uuid(),i.id,c.channel,CASE WHEN c.channel='Web' THEN 'Sent' ELSE 'Pending' END,0,
             CASE WHEN c.channel='Web' THEN clock_timestamp() END,clock_timestamp(),clock_timestamp()
            FROM inserted i CROSS JOIN (VALUES('Web'),('Email')) c(channel)
            """,ct);
        await tx.CommitAsync(ct);
        return created/2;
    }

    /// <summary>Sends pending emails outside any transaction; a renewal committed meanwhile suppresses the email.</summary>
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        var max=options.Value.MaxAttempts;
        List<PendingEmail> batch;
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            batch=await db.Database.SqlQuery<PendingEmail>($"""
                SELECT d.id AS "DeliveryId",n.entitlement_id AS "EntitlementId",n.building_id AS "BuildingId",u.email AS "Recipient",n.title AS "Title",n.body AS "Body",n.reference_ends_at AS "ReferenceEndsAt"
                FROM notification_deliveries d JOIN organization_notifications n ON n.id=d.notification_id JOIN users u ON u.id=n.recipient_user_id
                WHERE d.channel='Email' AND d.status='Pending' AND d.attempts<{max} ORDER BY d.created_at,d.id LIMIT 20 FOR UPDATE OF d SKIP LOCKED
                """).ToListAsync(ct);
            foreach(var item in batch)
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_deliveries SET attempts=attempts+1,updated_at=clock_timestamp() WHERE id={item.DeliveryId}",ct);
            await tx.CommitAsync(ct);
        }
        var sent=0;
        foreach(var item in batch)
        {
            var renewed=await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM service_entitlements r WHERE r.building_id={item.BuildingId} AND r.id<>{item.EntitlementId}
                 AND r.payment_transaction_id IS NOT NULL AND r.status IN('Active','Suspended') AND r.starts_at>={item.ReferenceEndsAt}) AS "Value"
                """).SingleAsync(ct);
            if(renewed)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_deliveries SET status='Failed',last_error='SUPPRESSED_RENEWED',updated_at=clock_timestamp() WHERE id={item.DeliveryId} AND status='Pending'",ct);
                continue;
            }
            try
            {
                await email.SendAsync(item.Recipient,item.Title,$"<p>{WebUtility.HtmlEncode(item.Body)}</p>",ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_deliveries SET status='Sent',sent_at=clock_timestamp(),last_error=NULL,updated_at=clock_timestamp() WHERE id={item.DeliveryId} AND status='Pending'",ct);
                sent++;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                // Never log recipient or body; keep the delivery Pending until attempts are exhausted.
                logger.LogWarning("Billing reminder delivery {DeliveryId} failed with {ErrorType}",item.DeliveryId,ex.GetType().Name);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_deliveries SET status=CASE WHEN attempts>={max} THEN 'Failed' ELSE 'Pending' END,last_error='EMAIL_UNAVAILABLE',updated_at=clock_timestamp() WHERE id={item.DeliveryId}",ct);
            }
        }
        return sent;
    }
}

public sealed class BillingReminderWorker(IServiceScopeFactory scopes,IOptions<BillingReminderOptions> options,ILogger<BillingReminderWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!options.Value.Enabled)return;
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope=scopes.CreateAsyncScope();
                var reminders=scope.ServiceProvider.GetRequiredService<BillingReminders>();
                await reminders.ScheduleAsync(DateTime.UtcNow,stoppingToken);
                await reminders.DispatchAsync(stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Billing reminder cycle failed with {ErrorType}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds,30,3600)),stoppingToken);
        }
    }
}
