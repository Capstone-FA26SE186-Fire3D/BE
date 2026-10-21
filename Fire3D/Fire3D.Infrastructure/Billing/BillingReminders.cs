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
    public int LeaseSeconds { get; set; } = 300;
}

/// <summary>
/// Building service expiry reminders. Scheduling writes one notification per recipient and entitlement period with Web and Email
/// deliveries in the same transaction (the outbox). Dispatch rechecks renewal before sending; email is at-least-once.
/// </summary>
public sealed class BillingReminders(Fire3DDbContext db,IEmailService email,IOptions<BillingReminderOptions> options,ILogger<BillingReminders> logger)
{
    // Same layout as the other Vietnamese FET3D emails; title and body come from the stored notification.
    internal static string Html(string title,string body)=>$"""
        <!doctype html><html lang="vi"><body style="font-family:Arial,sans-serif;color:#222">
        <h1 style="color:#d32f2f">FET3D</h1>
        <h2>{WebUtility.HtmlEncode(title)}</h2>
        <p>{WebUtility.HtmlEncode(body)}</p>
        </body></html>
        """;
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
             SELECT gen_random_uuid(),d.organization_id,u.id,d.building_id,d.id,'BuildingServiceExpiring','Dịch vụ tòa nhà sắp hết hạn',
              'Dịch vụ của tòa nhà '||d.name||' hết hạn lúc '||to_char(d.ends_at AT TIME ZONE 'Asia/Ho_Chi_Minh','HH24:MI DD/MM/YYYY')||' (giờ Việt Nam). Hãy gia hạn trước thời điểm này để học viên tiếp tục luyện tập.',
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

    /// <summary>
    /// Claims email deliveries with a lease (status Sending, token, expiry) in one committed statement, sends outside any
    /// transaction and completes or retries only while that lease is still held. A worker that crashed after claiming leaves an
    /// expired lease that another worker reclaims; a crash after the provider accepted the email can send it twice
    /// (at-least-once). Recipient, tenant and renewal are rechecked just before sending.
    /// </summary>
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        var max=options.Value.MaxAttempts;var lease=Math.Clamp(options.Value.LeaseSeconds,30,3600);
        // A delivery that lost its lease on the last allowed attempt can never be claimed again: close it instead of leaving it Sending.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_deliveries SET status='Failed',last_error='LEASE_EXPIRED_AFTER_MAX_ATTEMPTS',lease_token=NULL,lease_until=NULL,updated_at=clock_timestamp() WHERE channel='Email' AND status='Sending' AND lease_until<=clock_timestamp() AND attempts>={max}",ct);
        var batch=await db.Database.SqlQuery<ClaimedEmail>($"""
            WITH due AS (
             SELECT d.id FROM notification_deliveries d WHERE d.channel='Email' AND d.attempts<{max}
              AND ((d.status='Pending' AND d.next_attempt_at<=clock_timestamp()) OR (d.status='Sending' AND d.lease_until<=clock_timestamp()))
             ORDER BY d.next_attempt_at,d.created_at,d.id LIMIT 20 FOR UPDATE SKIP LOCKED)
            UPDATE notification_deliveries d SET status='Sending',lease_token=gen_random_uuid(),lease_until=clock_timestamp()+make_interval(secs=>{lease}),
             attempts=d.attempts+1,updated_at=clock_timestamp() FROM due WHERE d.id=due.id
            RETURNING d.id AS "DeliveryId",d.lease_token AS "LeaseToken",d.attempts AS "Attempts"
            """).ToListAsync(ct);
        var sent=0;
        foreach(var claim in batch)
        {
            var item=await db.Database.SqlQuery<PendingEmail>($"""
                SELECT d.id AS "DeliveryId",n.entitlement_id AS "EntitlementId",n.building_id AS "BuildingId",u.email AS "Recipient",n.title AS "Title",n.body AS "Body",n.reference_ends_at AS "ReferenceEndsAt"
                FROM notification_deliveries d JOIN organization_notifications n ON n.id=d.notification_id
                JOIN users u ON u.id=n.recipient_user_id AND u.organization_id=n.organization_id AND u.role='OrganizationUser' AND u.is_active AND u.deleted_at IS NULL
                 AND (u.registration_expires_at IS NULL OR u.email_verified_at IS NOT NULL)
                JOIN organizations o ON o.id=n.organization_id AND o.is_active AND o.deleted_at IS NULL
                JOIN buildings b ON b.id=n.building_id AND b.is_active AND b.deleted_at IS NULL
                WHERE d.id={claim.DeliveryId}
                """).SingleOrDefaultAsync(ct);
            if(item is null){await Finish(claim,"Failed","SUPPRESSED_RECIPIENT_UNAVAILABLE",ct);continue;}
            var renewed=await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM service_entitlements r WHERE r.building_id={item.BuildingId} AND r.id<>{item.EntitlementId}
                 AND r.payment_transaction_id IS NOT NULL AND r.status IN('Active','Suspended') AND r.starts_at>={item.ReferenceEndsAt}) AS "Value"
                """).SingleAsync(ct);
            if(renewed){await Finish(claim,"Failed","SUPPRESSED_RENEWED",ct);continue;}
            // The reminder is about a live period: a suspended, cancelled or already ended entitlement is not reminded.
            if(!await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM service_entitlements WHERE id={item.EntitlementId} AND status='Active' AND ends_at>clock_timestamp()) AS \"Value\"").SingleAsync(ct))
            {await Finish(claim,"Failed","SUPPRESSED_ENTITLEMENT_ENDED",ct);continue;}
            // Renew the lease right before sending; if another worker already reclaimed it, leave the delivery to that worker.
            if(await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_deliveries SET lease_until=clock_timestamp()+make_interval(secs=>{lease}),updated_at=clock_timestamp() WHERE id={claim.DeliveryId} AND status='Sending' AND lease_token={claim.LeaseToken} AND lease_until>clock_timestamp()",ct)!=1)continue;
            try
            {
                await email.SendAsync(item.Recipient,"[FET3D] "+item.Title,Html(item.Title,item.Body),ct);
                if(await Finish(claim,"Sent",null,ct))sent++;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                // Never log recipient or body; retry with backoff until attempts are exhausted.
                logger.LogWarning("Billing reminder delivery {DeliveryId} failed with {ErrorType}",claim.DeliveryId,ex.GetType().Name);
                await Finish(claim,claim.Attempts>=max?"Failed":"Pending","EMAIL_UNAVAILABLE",ct);
            }
        }
        return sent;
    }
    private sealed record ClaimedEmail(Guid DeliveryId,Guid LeaseToken,int Attempts);
    // Completes only while this worker still holds the lease; a reclaimed delivery is left to its new owner.
    private async Task<bool> Finish(ClaimedEmail claim,string status,string? error,CancellationToken ct)=>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notification_deliveries SET status={status},last_error={error},lease_token=NULL,lease_until=NULL,
             sent_at=CASE WHEN {status}='Sent' THEN clock_timestamp() ELSE sent_at END,
             next_attempt_at=CASE WHEN {status}='Pending' THEN clock_timestamp()+make_interval(secs=>least(3600,60*power(2,least({claim.Attempts}-1,6)))::double precision) ELSE next_attempt_at END,
             updated_at=clock_timestamp()
            WHERE id={claim.DeliveryId} AND status='Sending' AND lease_token={claim.LeaseToken} AND lease_until>clock_timestamp()
            """,ct)==1;
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
