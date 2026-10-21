using System.Net;
using System.Text.Json;
using Fire3D.Application.Email;
using Fire3D.Infrastructure.Billing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingNotificationTests
{
    private sealed class CountingEmail : IEmailService
    {
        public int Sent;public Func<Task>? During;public string? Subject,Html;
        public async Task SendAsync(string to,string subject,string html,CancellationToken ct){if(During is not null)await During();Subject=subject;Html=html;Interlocked.Increment(ref Sent);}
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
}
