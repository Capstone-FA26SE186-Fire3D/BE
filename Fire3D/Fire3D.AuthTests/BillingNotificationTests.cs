using System.Net;
using System.Text.Json;
using Fire3D.Application.Email;
using Fire3D.Application.Notifications;
using Fire3D.Infrastructure.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingNotificationTests
{
    private sealed class CountingEmail : IEmailService
    {
        public int Sent;public Func<Task>? During;public Func<CancellationToken,Task>? Hold;public string? Subject,Html;
        public async Task SendAsync(string to,string subject,string html,CancellationToken ct)
        {if(During is not null)await During();if(Hold is not null)await Hold(ct);Subject=subject;Html=html;Interlocked.Increment(ref Sent);}
    }
    private sealed class CountingPush : INotificationService
    {
        public int Calls;public int Delivered=1;public Guid? User;public IDictionary<string,string>? Data;public Func<Exception>? Fail;
        public Task<bool> SendPushAsync(string fcmToken,string title,string body,IDictionary<string,string>? data=null,CancellationToken ct=default)=>Task.FromResult(true);
        public Task<int> SendPushToUserAsync(Guid userId,string title,string body,IDictionary<string,string>? data=null,CancellationToken ct=default)
        {Calls++;User=userId;Data=data;return Fail is null?Task.FromResult(Delivered):Task.FromException<int>(Fail());}
    }
    private static async Task<(BillingApiTests.Factory Factory,Guid Entitlement)> ExpiringEntitlement(BillingDatabase db)
    {
        var (factory,entitlement,_)=await BillingUpgradeTopUpTests.PaidEntitlement(db);
        await db.Sql($"ALTER TABLE service_entitlements DISABLE TRIGGER USER; UPDATE service_entitlements SET starts_at=now()-interval '170 days',ends_at=now()+interval '3 days' WHERE id='{entitlement}'; ALTER TABLE service_entitlements ENABLE TRIGGER USER");
        return (factory,entitlement);
    }
    [BillingPostgresFact]
    public async Task Two_workers_never_send_the_same_reminder_and_a_crashed_claim_is_reclaimed()
    {
        await using var db=await BillingDatabase.Create();var (seedFactory,_)=await ExpiringEntitlement(db);using var __=seedFactory;
        var options=Options.Create(new BillingReminderOptions{Enabled=true,LeadDays=5,MaxAttempts=3,LeaseSeconds=30});
        var email=new CountingEmail();
        await using var a=db.Context();await using var b=db.Context();
        var first=new BillingReminders(a,email,options,NullLogger<BillingReminders>.Instance);var second=new BillingReminders(b,email,options,NullLogger<BillingReminders>.Instance);
        Assert.Equal(1,await first.ScheduleAsync(DateTime.UtcNow,default));
        // The second worker runs while the first holds the lease and is sending: it finds nothing to claim.
        email.During=async()=>{email.During=null;Assert.Equal(0,await second.DispatchAsync(default));};
        Assert.Equal(1,await first.DispatchAsync(default));
        Assert.Equal(1,email.Sent);
        Assert.Equal("Sent",await db.Scalar("SELECT status FROM notification_deliveries WHERE channel='Email'"));
        // The stored notification and the email are Vietnamese, with the end time in Vietnam time.
        Assert.Equal("Dịch vụ tòa nhà sắp hết hạn",await db.Scalar("SELECT title FROM organization_notifications"));
        Assert.Contains("(giờ Việt Nam)",(string)(await db.Scalar("SELECT body FROM organization_notifications"))!);
        Assert.Equal("[FET3D] Dịch vụ tòa nhà sắp hết hạn",email.Subject);
        Assert.Contains("lang=\"vi\"",email.Html);
        // A worker that crashed after claiming leaves an expired lease; the next worker reclaims it (at-least-once).
        await db.Sql("UPDATE notification_deliveries SET status='Sending',sent_at=NULL,lease_token=gen_random_uuid(),lease_until=now()-interval '1 second' WHERE channel='Email'");
        Assert.Equal(1,await second.DispatchAsync(default));
        Assert.Equal(2,email.Sent);
        // A completion from a worker whose lease was taken over is ignored.
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM notification_deliveries WHERE channel='Email' AND lease_token IS NULL AND status='Sent'"));
    }

    [BillingPostgresFact]
    public async Task Exhausted_expired_leases_fail_and_ended_entitlements_are_not_reminded()
    {
        await using var db=await BillingDatabase.Create();var (seedFactory,entitlement)=await ExpiringEntitlement(db);using var __=seedFactory;
        var options=Options.Create(new BillingReminderOptions{Enabled=true,LeadDays=5,MaxAttempts=3,LeaseSeconds=30});
        var email=new CountingEmail();
        await using var context=db.Context();var reminders=new BillingReminders(context,email,options,NullLogger<BillingReminders>.Instance);
        Assert.Equal(1,await reminders.ScheduleAsync(DateTime.UtcNow,default));
        // A worker crashed on its last attempt: the delivery leaves Sending instead of staying stuck there.
        await db.Sql("UPDATE notification_deliveries SET status='Sending',attempts=3,lease_token=gen_random_uuid(),lease_until=now()-interval '1 second' WHERE channel='Email'");
        Assert.Equal(0,await reminders.DispatchAsync(default));
        Assert.Equal("Failed",await db.Scalar("SELECT status FROM notification_deliveries WHERE channel='Email'"));
        Assert.Equal("LEASE_EXPIRED_AFTER_MAX_ATTEMPTS",await db.Scalar("SELECT last_error FROM notification_deliveries WHERE channel='Email'"));
        // An entitlement that ended (or was suspended) before sending is suppressed, not emailed.
        await db.Sql($"UPDATE notification_deliveries SET status='Pending',attempts=0,last_error=NULL,lease_token=NULL,lease_until=NULL,next_attempt_at=now() WHERE channel='Email'; ALTER TABLE service_entitlements DISABLE TRIGGER USER; UPDATE service_entitlements SET status='Suspended' WHERE id='{entitlement}'; ALTER TABLE service_entitlements ENABLE TRIGGER USER");
        await reminders.DispatchAsync(default);
        Assert.Equal(0,email.Sent);
        Assert.Equal("SUPPRESSED_ENTITLEMENT_ENDED",await db.Scalar("SELECT last_error FROM notification_deliveries WHERE channel='Email'"));
    }

    [BillingPostgresFact]
    public async Task Notifications_list_filter_and_mark_read_only_for_the_current_recipient()
    {
        await using var db=await BillingDatabase.Create();var (factory,entitlement)=await ExpiringEntitlement(db);using var ___=factory;
        using var owner=factory.As(BillingDatabase.Owner);using var other=factory.As(BillingDatabase.Other);using var admin=factory.As(BillingDatabase.Admin);
        await using(var context=db.Context())
            await new BillingReminders(context,new CountingEmail(),Options.Create(new BillingReminderOptions{Enabled=true,LeadDays=5}),NullLogger<BillingReminders>.Instance).ScheduleAsync(DateTime.UtcNow,default);
        var list=JsonDocument.Parse(await owner.GetStringAsync("/api/organizations/me/notifications?unread=true")).RootElement;
        var item=Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(entitlement,item.GetProperty("entitlementId").GetGuid());
        Assert.Equal("Building One",item.GetProperty("buildingName").GetString());
        Assert.True(item.GetProperty("stillRelevant").GetBoolean());
        var id=item.GetProperty("id").GetGuid();
        Assert.Equal(0,JsonDocument.Parse(await other.GetStringAsync("/api/organizations/me/notifications")).RootElement.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound,(await other.PatchAsync($"/api/organizations/me/notifications/{id}/read",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await admin.GetAsync("/api/organizations/me/notifications")).StatusCode);
        var read=await owner.PatchAsync($"/api/organizations/me/notifications/{id}/read",null);Assert.Equal(HttpStatusCode.OK,read.StatusCode);
        var readAt=JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("readAt").GetDateTime();
        Assert.Equal(readAt,JsonDocument.Parse(await (await owner.PatchAsync($"/api/organizations/me/notifications/{id}/read",null)).Content.ReadAsStringAsync()).RootElement.GetProperty("readAt").GetDateTime());
        Assert.Equal(0,JsonDocument.Parse(await owner.GetStringAsync("/api/organizations/me/notifications?unread=true")).RootElement.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.GetAsync("/api/organizations/me/notifications?type=Other")).StatusCode);
        // A renewal makes the reminder no longer actionable; the history stays.
        await db.Sql($"""
            ALTER TABLE service_entitlements DISABLE TRIGGER USER;
            INSERT INTO service_entitlements(id,organization_id,building_id,service_package_id,quotation_id,payment_transaction_id,provisioning_key,status,starts_at,ends_at,created_by,price_snapshot,terms_snapshot,created_at,playtest_units_granted,playtest_units_used,commercial_version,learner_limit)
            SELECT gen_random_uuid(),organization_id,building_id,service_package_id,quotation_id,payment_transaction_id,'renewal:'||gen_random_uuid(),'Suspended',ends_at,ends_at+interval '6 months',created_by,jsonb_build_object(),jsonb_build_object(),now(),0,0,commercial_version,learner_limit FROM service_entitlements WHERE id='{entitlement}';
            ALTER TABLE service_entitlements ENABLE TRIGGER USER;
            """);
        var after=JsonDocument.Parse(await owner.GetStringAsync("/api/organizations/me/notifications")).RootElement;
        Assert.Equal(1,after.GetProperty("total").GetInt32());Assert.False(after.GetProperty("items")[0].GetProperty("stillRelevant").GetBoolean());
    }
    [BillingPostgresFact]
    public async Task Push_reminder_goes_to_live_devices_only_and_closes_without_one()
    {
        await using var db=await BillingDatabase.Create();var (seedFactory,entitlement)=await ExpiringEntitlement(db);using var __=seedFactory;
        var options=Options.Create(new BillingReminderOptions{Enabled=true,LeadDays=5,MaxAttempts=3,LeaseSeconds=30});
        var recipient=(Guid)(await db.Scalar($"SELECT u.id FROM users u JOIN service_entitlements e ON e.organization_id=u.organization_id WHERE e.id='{entitlement}' AND u.role='OrganizationUser' ORDER BY u.id LIMIT 1"))!;
        await db.Sql($"INSERT INTO user_devices(user_id,device_uuid,fcm_token,notifications_enabled,fcm_token_generation) VALUES('{recipient}','device-1','fcm-live',true,1),('{recipient}','device-2','fcm-revoked',false,1)");
        var email=new CountingEmail();var push=new CountingPush();
        await using var context=db.Context();var reminders=new BillingReminders(context,email,options,NullLogger<BillingReminders>.Instance,push);
        Assert.Equal(1,await reminders.ScheduleAsync(DateTime.UtcNow,default));
        // Only the recipient with a live FCM binding gets a Push delivery next to Web and Email.
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM notification_deliveries WHERE channel='Push' AND status='Pending'"));
        Assert.Equal(2,await reminders.DispatchAsync(default));
        Assert.Equal(1,push.Calls);Assert.Equal(recipient,push.User);
        Assert.Equal("BuildingServiceExpiring",push.Data!["type"]);
        Assert.Equal("Sent",await db.Scalar("SELECT status FROM notification_deliveries WHERE channel='Push'"));
        // The device was revoked (logout-all) before a retry: the push delivery closes instead of retrying.
        await db.Sql("UPDATE user_devices SET notifications_enabled=false,fcm_token=NULL,revoked_at=now(); UPDATE notification_deliveries SET status='Pending',sent_at=NULL,next_attempt_at=now() WHERE channel='Push'");
        Assert.Equal(0,await reminders.DispatchAsync(default));
        Assert.Equal(1,push.Calls);
        Assert.Equal("NO_ACTIVE_DEVICE",await db.Scalar("SELECT last_error FROM notification_deliveries WHERE channel='Push' AND status='Failed'"));
        // A provider failure (no device reached) is retried with backoff like email.
        await db.Sql($"UPDATE user_devices SET notifications_enabled=true,fcm_token='fcm-new',revoked_at=NULL WHERE device_uuid='device-1'; UPDATE notification_deliveries SET status='Pending',attempts=0,last_error=NULL,next_attempt_at=now() WHERE channel='Push'");
        push.Delivered=0;
        Assert.Equal(0,await reminders.DispatchAsync(default));
        Assert.Equal("Pending",await db.Scalar("SELECT status FROM notification_deliveries WHERE channel='Push'"));
        Assert.Equal("PUSH_UNAVAILABLE",await db.Scalar("SELECT last_error FROM notification_deliveries WHERE channel='Push'"));
    }

    [BillingPostgresFact]
    public async Task Shutdown_mid_send_keeps_the_lease_and_a_push_provider_error_is_retried_with_backoff()
    {
        await using var db=await BillingDatabase.Create();var (seedFactory,_)=await ExpiringEntitlement(db);using var __=seedFactory;
        var options=Options.Create(new BillingReminderOptions{Enabled=true,LeadDays=5,MaxAttempts=3,LeaseSeconds=30});
        await db.Sql("INSERT INTO user_devices(user_id,device_uuid,fcm_token,notifications_enabled,fcm_token_generation) SELECT u.id,'device-1','fcm-live',true,1 FROM users u JOIN service_entitlements e ON e.organization_id=u.organization_id WHERE u.role='OrganizationUser'");
        var email=new CountingEmail();var push=new CountingPush();var log=new CapturedLog<BillingReminders>();
        await using var context=db.Context();var reminders=new BillingReminders(context,email,options,log,push);
        Assert.Equal(1,await reminders.ScheduleAsync(DateTime.UtcNow,default));
        // Park the push delivery so the first dispatch handles the email alone.
        await db.Sql("UPDATE notification_deliveries SET status='Failed' WHERE channel='Push'");
        using(var stopping=new CancellationTokenSource())
        {
            email.During=()=>{stopping.Cancel();throw new OperationCanceledException(stopping.Token);};
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reminders.DispatchAsync(stopping.Token));
        }
        // The claim stays leased (not failed, not retried early); once the lease expires another worker reclaims it.
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM notification_deliveries WHERE channel='Email' AND status='Sending' AND attempts=1 AND lease_token IS NOT NULL AND lease_until>now()"));
        email.During=null;push.Fail=()=>new HttpRequestException("fcm.googleapis.com unreachable");
        await db.Sql("UPDATE notification_deliveries SET lease_until=now()-interval '1 second' WHERE channel='Email'; UPDATE notification_deliveries SET status='Pending',next_attempt_at=now() WHERE channel='Push'");
        Assert.Equal(1,await reminders.DispatchAsync(default));
        Assert.Equal(1,email.Sent);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM notification_deliveries WHERE channel='Email' AND status='Sent' AND attempts=2"));
        // A push provider failure is a retryable delivery error: Pending again, with a backoff, lease released.
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM notification_deliveries WHERE channel='Push' AND status='Pending' AND last_error='PUSH_UNAVAILABLE' AND lease_token IS NULL AND next_attempt_at>now()+interval '30 seconds'"));
        Assert.Equal("Billing reminder push "+await db.Scalar("SELECT id FROM notification_deliveries WHERE channel='Push'")+" failed with HttpRequestException",
            Assert.Single(log.Entries,x=>x.Level==Microsoft.Extensions.Logging.LogLevel.Warning).Message);
        Assert.DoesNotContain("owner@example.test",log.All());
        // Shutdown during the push call propagates and leaves the push claim leased as well.
        await db.Sql("UPDATE notification_deliveries SET next_attempt_at=now() WHERE channel='Push'");
        using(var stopping=new CancellationTokenSource())
        {
            push.Fail=()=>{stopping.Cancel();return new OperationCanceledException(stopping.Token);};
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reminders.DispatchAsync(stopping.Token));
        }
        Assert.Equal("Sending",await db.Scalar("SELECT status FROM notification_deliveries WHERE channel='Push'"));
    }

    [BillingPostgresFact]
    public async Task Reminder_worker_schedules_and_dispatches_each_cycle_and_survives_a_failed_cycle()
    {
        // Disabled: the worker returns without touching its scope.
        var disabledLog=new CapturedLog<BillingReminderWorker>();
        var disabled=new BillingReminderWorker(WorkerHarness.Scopes(_=>{}),Options.Create(new BillingReminderOptions{Enabled=false}),disabledLog);
        await disabled.StartAsync(default);await disabled.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));await disabled.StopAsync(default);
        Assert.Empty(disabledLog.Entries);
        // A failed cycle is logged by type and the worker waits for the next poll; shutdown cancels that wait.
        var failedLog=new CapturedLog<BillingReminderWorker>();var failed=WorkerHarness.Signal();failedLog.Logged=(_,_)=>failed.TrySetResult();
        var broken=new BillingReminderWorker(WorkerHarness.Scopes(_=>{}),Options.Create(new BillingReminderOptions{Enabled=true}),failedLog);
        await WorkerHarness.Run(broken,failed.Task);
        Assert.Equal("Billing reminder cycle failed with InvalidOperationException",Assert.Single(failedLog.Entries).Message);
        Assert.False(broken.ExecuteTask!.IsFaulted);
        // Live cycles: the worker schedules the expiring entitlement and emails it through its own scope.
        await using var db=await BillingDatabase.Create();var (seedFactory,_)=await ExpiringEntitlement(db);using var __=seedFactory;
        var email=new CountingEmail();var inFlight=WorkerHarness.Signal();
        var options=Options.Create(new BillingReminderOptions{Enabled=true,LeadDays=5,PollSeconds=1});
        var scopes=WorkerHarness.Scopes(s=>s.AddLogging().AddSingleton(options).AddSingleton<IEmailService>(email)
            .AddScoped(_=>db.Context()).AddScoped<BillingReminders>());
        // Shutdown in the middle of a send ends the loop cleanly; the claimed delivery keeps its lease for the next worker.
        var log=new CapturedLog<BillingReminderWorker>();var first=new BillingReminderWorker(scopes,options,log);
        email.Hold=ct=>{inFlight.TrySetResult();return WorkerHarness.UntilStopped(ct);};
        await WorkerHarness.Run(first,inFlight.Task);
        Assert.True(first.ExecuteTask!.IsCompletedSuccessfully);Assert.Empty(log.Entries);Assert.Equal(0,email.Sent);
        Assert.Equal("Sending",await db.Scalar("SELECT status FROM notification_deliveries WHERE channel='Email'"));
        await db.Sql("UPDATE notification_deliveries SET lease_until=now()-interval '1 second' WHERE channel='Email'");
        var sent=WorkerHarness.Signal();email.Hold=null;email.During=()=>{sent.TrySetResult();return Task.CompletedTask;};
        var worker=new BillingReminderWorker(scopes,options,log);
        await worker.StartAsync(default);
        try
        {
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(!Equals("Sent",await db.Scalar("SELECT status FROM notification_deliveries WHERE channel='Email'")))
            {Assert.True(DateTime.UtcNow<deadline,"The email delivery was not completed.");await Task.Delay(50);}
        }
        finally{await worker.StopAsync(default);}
        Assert.Equal(1,email.Sent);Assert.Empty(log.Entries);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM organization_notifications"));
    }
}
