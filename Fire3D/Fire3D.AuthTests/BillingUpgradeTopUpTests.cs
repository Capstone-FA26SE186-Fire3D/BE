using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Application.Email;
using Fire3D.Infrastructure.Billing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingUpgradeTopUpTests
{
    private static string ETag(QuotationResponse q)=>BillingETag.Format(q.Id,q.Revision);

    // A paid v7 entitlement (learner limit 20) for the fixture Building, provisioned through the real PayOS gates.
    private static async Task<(BillingApiTests.Factory Factory,Guid Entitlement,Guid Policy)> PaidEntitlement(BillingDatabase db,FakePayos? provider=null)
    {
        var quote=await PayosCheckoutTests.Accepted(db);
        var factory=new BillingApiTests.Factory(db,provider??new FakePayos());
        await PayosProvisioningTests.Paid(factory,quote,"base");await PayosWebhookTests.Recover(factory,db);
        var entitlement=(Guid)(await db.Scalar("SELECT id FROM service_entitlements"))!;
        await using var context=db.Context();
        var policy=await new BillingService(context).CreateQuotaPolicy(BillingDatabase.Admin,BillingDatabase.Admin,new("tokens",DateTimeOffset.UtcNow.AddDays(-1)),default);
        return (factory,entitlement,policy.Id);
    }

    private static async Task<QuotationResponse> AcceptedUpgrade(BillingDatabase db,Guid entitlement,Guid policy,int limit,decimal price,int extraQuota=0,string? key=null)
    {
        await using var context=db.Context();var billing=new BillingService(context);
        var package=await billing.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("UP"+Guid.NewGuid().ToString("N")[..10],"Upgrade target",3000,6,true,null,limit,0),null,default);
        var quote=await billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,package.Id,"Upgrade",entitlement)]),key??Guid.NewGuid().ToString("N"),default);
        Assert.Equal("OneTime",quote.Items[0].PricingBasis);Assert.Equal(0m,quote.TotalAmount);Assert.Equal(20,quote.Items[0].UpgradePreviousLearnerLimit);
        var validUntil=DateTimeOffset.UtcNow.AddHours(1);
        quote=await billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"Upgrade terms",validUntil,
            [new(quote.Items[0].Id,validUntil.AddHours(1),limit,extraQuota,extraQuota>0?policy:null,price)]),ETag(quote),default);
        return await billing.AcceptQuotation(BillingDatabase.Owner,BillingDatabase.Owner,quote.Id,ETag(quote),default);
    }

    [BillingPostgresFact]
    public async Task Upgrade_keeps_period_and_seats_raises_limit_once_and_bundles_quota()
    {
        await using var db=await BillingDatabase.Create();var (factory,entitlement,policy)=await PaidEntitlement(db);using var _=factory;
        var before=await db.Scalar($"SELECT ends_at FROM service_entitlements WHERE id='{entitlement}'");
        await db.Sql($"INSERT INTO billing_learner_seats(entitlement_id,organization_id,building_id,trainee_id,first_session_id,allocated_at) VALUES('{entitlement}','{BillingDatabase.Org}','{BillingDatabase.Building}','{BillingDatabase.Other}',gen_random_uuid(),now())");
        var upgrade=await AcceptedUpgrade(db,entitlement,policy,40,500000,extraQuota:30);
        Assert.Equal(500000m,upgrade.TotalAmount);Assert.Equal(40,upgrade.Items[0].LearnerLimit);Assert.Equal(before,upgrade.Items[0].EndsAt);
        await PayosProvisioningTests.Paid(factory,upgrade.Id,"upgrade");
        await Task.WhenAll(PayosWebhookTests.Recover(factory,db),PayosWebhookTests.Recover(factory,db));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(before,await db.Scalar($"SELECT ends_at FROM service_entitlements WHERE id='{entitlement}'"));
        Assert.Equal(1L,await db.Scalar($"SELECT count(*) FROM billing_entitlement_upgrades WHERE entitlement_id='{entitlement}' AND capacity_revision=1 AND previous_learner_limit=20 AND learner_limit=40"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_ai_quota_grants WHERE source_kind='Upgrade' AND quota_units=30"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_upgrade_reservations WHERE status='Consumed'"));
        await PayosWebhookTests.Recover(factory,db);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_entitlement_upgrades"));
        // Before the effective time the base limit applies; after it the raised limit, with the same consumed seat.
        Assert.Equal(20,await db.Scalar($"SELECT billing_effective_learner_limit('{entitlement}',now())"));
        Assert.Equal(40,await db.Scalar($"SELECT billing_effective_learner_limit('{entitlement}',now()+interval '3 hours')"));
        using var owner=factory.As(BillingDatabase.Owner);
        var view=await PayosCheckoutTests.Json(await owner.GetAsync($"/api/buildings/{BillingDatabase.Building}/service-entitlement"));
        // The fixture period starts in two days, so it is reported as upcoming with the limit effective at its start.
        Assert.Equal(JsonValueKind.Null,view.GetProperty("current").ValueKind);
        var upcoming=view.GetProperty("upcoming")[0];
        Assert.Equal(1,upcoming.GetProperty("capacityRevision").GetInt32());Assert.Equal(1,upcoming.GetProperty("seatsUsed").GetInt32());
        Assert.Equal(40,upcoming.GetProperty("effectiveLearnerLimit").GetInt32());Assert.Equal(39,upcoming.GetProperty("seatsRemaining").GetInt32());
        Assert.False(upcoming.GetProperty("isEffective").GetBoolean());
        Assert.Single(upcoming.GetProperty("upgrades").EnumerateArray());
        using var other=factory.As(BillingDatabase.Other);
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/buildings/{BillingDatabase.Building}/service-entitlement")).StatusCode);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Sql("UPDATE billing_entitlement_upgrades SET learner_limit=99"));
    }

    [BillingPostgresFact]
    public async Task Two_upgrades_on_one_baseline_reserve_once_and_a_late_payment_needs_reconcile()
    {
        await using var db=await BillingDatabase.Create();var (factory,entitlement,policy)=await PaidEntitlement(db);using var _=factory;
        var a=await AcceptedUpgrade(db,entitlement,policy,30,100000);var b=await AcceptedUpgrade(db,entitlement,policy,35,120000);
        using var owner=factory.As(BillingDatabase.Owner);
        var results=await Task.WhenAll(owner.SendAsync(PayosCheckoutTests.Create(a.Id,"up-a")),owner.SendAsync(PayosCheckoutTests.Create(b.Id,"up-b")));
        Assert.Single(results,x=>x.StatusCode==HttpStatusCode.Created);
        var loser=results.Single(x=>x.StatusCode==HttpStatusCode.Conflict);Assert.Contains("PAYOS_UPGRADE_RESERVED",await loser.Content.ReadAsStringAsync());
        // Simulate an operator applying the competing baseline change before the winner's payment lands: money stays Applied, nothing is applied.
        var winner=await PayosCheckoutTests.Json(results.Single(x=>x.IsSuccessStatusCode));var order=winner.GetProperty("orderCode").GetInt64();
        await db.Sql($"""
            INSERT INTO billing_entitlement_upgrades(entitlement_id,capacity_revision,previous_learner_limit,learner_limit,additional_quota_units,effective_from,quotation_item_id,payment_transaction_id,provisioning_key)
            SELECT '{entitlement}',1,20,25,0,now()+interval '1 hour',i.id,e.payment_transaction_id,'fixture-baseline' FROM service_entitlements e JOIN quotation_building_items i ON i.id=e.quotation_item_id WHERE e.id='{entitlement}'
            """);
        using var webhook=factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,(await webhook.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(order,(long)winner.GetProperty("amount").GetDecimal(),"VND","link"+order,"late-upgrade","2026-10-02 18:00:00"))).StatusCode);
        await PayosWebhookTests.Recover(factory,db);
        Assert.Equal("Applied",await db.Scalar("SELECT status::text FROM payment_transactions WHERE provider_transaction_id='late-upgrade'"));
        Assert.Equal("NeedsReconcile",await db.Scalar("SELECT r.status FROM payment_provisioning_records r JOIN payment_transactions t ON t.id=r.payment_transaction_id WHERE t.provider_transaction_id='late-upgrade'"));
        Assert.Equal("PAYOS_UPGRADE_NEEDS_RECONCILE",await db.Scalar("SELECT r.last_error FROM payment_provisioning_records r JOIN payment_transactions t ON t.id=r.payment_transaction_id WHERE t.provider_transaction_id='late-upgrade'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_entitlement_upgrades"));
    }

    [BillingPostgresFact]
    public async Task Upgrade_draft_and_issue_validation_rejects_lower_limits_mixing_and_missing_fee()
    {
        await using var db=await BillingDatabase.Create();var (factory,entitlement,policy)=await PaidEntitlement(db);using var _=factory;
        await using var context=db.Context();var billing=new BillingService(context);
        var low=await billing.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("LOW","Low",1000,6,true,null,10,0),null,default);
        Assert.Equal("UPGRADE_LIMIT_NOT_HIGHER",(await Assert.ThrowsAsync<BillingException>(()=>billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,low.Id,"Upgrade",entitlement)]),"low",default))).Code);
        Assert.Equal("UPGRADE_ENTITLEMENT_INVALID",(await Assert.ThrowsAsync<BillingException>(()=>billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,low.Id,"Upgrade",Guid.NewGuid())]),"missing",default))).Code);
        Assert.Equal("BILLING_VALIDATION_FAILED",(await Assert.ThrowsAsync<BillingException>(()=>billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,low.Id,"Upgrade")]),"no-target",default))).Code);
        var high=await billing.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("HIGH","High",1000,6,true,null,50,0),null,default);
        var quote=await billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,high.Id,"Upgrade",entitlement)]),"high",default);
        var validUntil=DateTimeOffset.UtcNow.AddHours(1);
        var noFee=await Assert.ThrowsAsync<BillingException>(()=>billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"t",validUntil,[new(quote.Items[0].Id,validUntil.AddHours(1))]),ETag(quote),default));
        Assert.Contains("oneTimePrice",noFee.Message);
        var lower=await Assert.ThrowsAsync<BillingException>(()=>billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"t",validUntil,[new(quote.Items[0].Id,validUntil.AddHours(1),20,0,null,1000)]),ETag(quote),default));
        Assert.Contains("exceed",lower.Message);
        var beyond=await Assert.ThrowsAsync<BillingException>(()=>billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"t",validUntil,[new(quote.Items[0].Id,validUntil.AddYears(2),50,0,null,1000)]),ETag(quote),default));
        Assert.Contains("entitlement end",beyond.Message);
        var noPolicy=await Assert.ThrowsAsync<BillingException>(()=>billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"t",validUntil,[new(quote.Items[0].Id,validUntil.AddHours(1),50,10,null,1000)]),ETag(quote),default));
        Assert.Equal("BILLING_POLICY_INTERVAL_INVALID",noPolicy.Code);
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM quotations WHERE status='Issued'"));
    }

    private static async Task<QuotationResponse> AcceptedTopUp(BillingDatabase db,Guid policy,int units,decimal amount,string key="topup")
    {
        await using var context=db.Context();var billing=new BillingService(context);
        var quote=await billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new(null,"AIQuotaTopUp",new(units)),key,default);
        Assert.Equal("AIQuotaTopUp",quote.Purpose);Assert.Empty(quote.Items);Assert.Equal(units,quote.TopUp!.RequestedQuotaUnits);
        var validUntil=DateTimeOffset.UtcNow.AddHours(1);
        quote=await billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(1000,"Top-up terms",validUntil,null,
            new(policy,units,amount,validUntil.AddHours(1),validUntil.AddDays(30))),ETag(quote),default);
        Assert.Equal(amount+1000,quote.TotalAmount);Assert.Equal(await db.Scalar($"SELECT quota_unit FROM billing_quota_policy_versions WHERE id='{policy}'"),quote.TopUp!.QuotaUnit);
        return await billing.AcceptQuotation(BillingDatabase.Owner,BillingDatabase.Owner,quote.Id,ETag(quote),default);
    }

    [BillingPostgresFact]
    public async Task Top_up_grants_quota_once_without_entitlement_or_service_extension_and_balance_is_per_unit()
    {
        await using var db=await BillingDatabase.Create();var (factory,entitlement,policy)=await PaidEntitlement(db);using var _=factory;
        var endsBefore=await db.Scalar($"SELECT ends_at FROM service_entitlements WHERE id='{entitlement}'");
        var topUp=await AcceptedTopUp(db,policy,500,250000);
        var checkout=await PayosProvisioningTests.Paid(factory,topUp.Id,"topup");
        using var webhook=factory.CreateClient();var order=checkout.GetProperty("orderCode").GetInt64();
        // Duplicate verified delivery of the same payment.
        Assert.Equal(HttpStatusCode.OK,(await webhook.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(order,(long)checkout.GetProperty("amount").GetDecimal(),"VND","link"+order,"topup","2026-10-02 18:00:00"))).StatusCode);
        await Task.WhenAll(PayosWebhookTests.Recover(factory,db),PayosWebhookTests.Recover(factory,db));
        await PayosWebhookTests.Recover(factory,db);
        Assert.True(Equals(1L,await db.Scalar("SELECT count(*) FROM billing_ai_quota_grants WHERE source_kind='TopUp' AND quota_units=500 AND entitlement_id IS NULL")),
            "Top-up provisioning: "+await db.Scalar("SELECT string_agg(provisioning_key||'='||status||':'||coalesce(last_error,''),';') FROM payment_provisioning_records")
            +" inbox: "+await db.Scalar("SELECT string_agg(status||':'||coalesce(last_error,''),';') FROM payos_webhook_inbox")
            +" tx: "+await db.Scalar("SELECT string_agg(status::text||':'||coalesce(rejection_reason,''),';') FROM payment_transactions"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(endsBefore,await db.Scalar($"SELECT ends_at FROM service_entitlements WHERE id='{entitlement}'"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM billing_service_reservations WHERE status='Reserved'"));
        // Another unit never combines with tokens.
        await db.Sql($"""
            INSERT INTO billing_quota_policy_versions(id,audience,policy_kind,quota_unit,effective_from,rollover,created_by,created_at) VALUES(gen_random_uuid(),'organization','quota','requests',now()-interval '1 day','None','{BillingDatabase.Admin}',now());
            """);
        var requestsPolicy=(Guid)(await db.Scalar("SELECT id FROM billing_quota_policy_versions WHERE quota_unit='requests'"))!;
        var second=await AcceptedTopUp(db,requestsPolicy,7,9000,"topup-requests");
        await PayosProvisioningTests.Paid(factory,second.Id,"topup-requests");await PayosWebhookTests.Recover(factory,db);
        await db.Sql("UPDATE billing_ai_quota_grants SET quota_units=quota_units WHERE false");
        await db.Sql($"""
            ALTER TABLE billing_ai_quota_grants DISABLE TRIGGER billing_quota_grant_immutable;
            UPDATE billing_ai_quota_grants SET starts_at=now()-interval '1 minute' WHERE source_kind='TopUp';
            ALTER TABLE billing_ai_quota_grants ENABLE TRIGGER billing_quota_grant_immutable;
            INSERT INTO billing_ai_quota_allocations(grant_id,organization_id,request_id,quota_unit,reserved_units,status) SELECT id,organization_id,gen_random_uuid(),quota_unit,40,'Reserved' FROM billing_ai_quota_grants WHERE quota_unit='tokens';
            INSERT INTO billing_ai_quota_allocations(grant_id,organization_id,request_id,quota_unit,reserved_units,consumed_units,status,settled_at) SELECT id,organization_id,gen_random_uuid(),quota_unit,60,55,'Settled',now() FROM billing_ai_quota_grants WHERE quota_unit='tokens';
            """);
        using var owner=factory.As(BillingDatabase.Owner);using var admin=factory.As(BillingDatabase.Admin);using var other=factory.As(BillingDatabase.Other);
        var balance=await PayosCheckoutTests.Json(await owner.GetAsync("/api/organizations/me/ai-quota"));
        var units=balance.GetProperty("units").EnumerateArray().ToDictionary(x=>x.GetProperty("quotaUnit").GetString()!);
        Assert.Equal(["requests","tokens"],units.Keys.Order());
        Assert.Equal(500,units["tokens"].GetProperty("granted").GetInt64());Assert.Equal(40,units["tokens"].GetProperty("reserved").GetInt64());
        Assert.Equal(55,units["tokens"].GetProperty("consumed").GetInt64());Assert.Equal(405,units["tokens"].GetProperty("available").GetInt64());
        Assert.Equal(7,units["requests"].GetProperty("available").GetInt64());
        var grants=await PayosCheckoutTests.Json(await owner.GetAsync("/api/organizations/me/ai-quota/grants?pageSize=1"));
        Assert.Equal(2,grants.GetProperty("total").GetInt32());Assert.Single(grants.GetProperty("items").EnumerateArray());
        var usage=await PayosCheckoutTests.Json(await owner.GetAsync("/api/organizations/me/ai-usage"));Assert.Equal(2,usage.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK,(await admin.GetAsync($"/api/admin/organizations/{BillingDatabase.Org}/ai-quota")).StatusCode);
        Assert.Empty((await PayosCheckoutTests.Json(await other.GetAsync("/api/organizations/me/ai-quota"))).GetProperty("units").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.GetAsync($"/api/admin/organizations/{BillingDatabase.Org}/ai-quota")).StatusCode);
    }

    [BillingPostgresFact]
    public async Task Top_up_issue_requires_policy_coverage_and_no_building_lines()
    {
        await using var db=await BillingDatabase.Create();await using var context=db.Context();var billing=new BillingService(context);
        var policy=await billing.CreateQuotaPolicy(BillingDatabase.Admin,BillingDatabase.Admin,new("tokens",DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(10)),default);
        Assert.Equal("BILLING_VALIDATION_FAILED",(await Assert.ThrowsAsync<BillingException>(()=>billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,Guid.NewGuid(),"New")],"AIQuotaTopUp"),"mixed",default))).Code);
        var quote=await billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new(null,"AIQuotaTopUp"),"t1",default);
        var validUntil=DateTimeOffset.UtcNow.AddHours(1);
        var beyond=await Assert.ThrowsAsync<BillingException>(()=>billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"t",validUntil,null,new(policy.Id,10,1000,validUntil.AddHours(1),validUntil.AddDays(30))),ETag(quote),default));
        Assert.Equal("BILLING_POLICY_INTERVAL_INVALID",beyond.Code);
        var early=await Assert.ThrowsAsync<BillingException>(()=>billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"t",validUntil,null,new(policy.Id,10,1000,validUntil.AddMinutes(-30),validUntil.AddDays(5))),ETag(quote),default));
        Assert.Contains("payment deadline",early.Message);
        var issued=await billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"t",validUntil,null,new(policy.Id,10,1000,validUntil,validUntil.AddDays(5))),ETag(quote),default);
        Assert.Equal("Issued",issued.Status);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Sql($"UPDATE quotation_topup_items SET quota_units=99 WHERE quotation_id='{quote.Id}'"));
    }

    [BillingPostgresFact]
    public async Task Enterprise_request_admin_detail_status_etag_and_quotation_without_service_grant()
    {
        await using var db=await BillingDatabase.Create();await using var context=db.Context();var billing=new BillingService(context);
        var request=await billing.CreateEnterpriseRequest(BillingDatabase.Owner,BillingDatabase.Owner,new(5,12,"Contact","contact@example.test"),"ent",default);
        var detail=await billing.GetEnterpriseRequest(BillingDatabase.Admin,request.Id,default);Assert.Equal(1,detail.Revision);
        Assert.Equal(412,(await Assert.ThrowsAsync<BillingException>(()=>billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Contacted"),BillingETag.Format(request.Id,9),default))).Status);
        Assert.Equal(428,(await Assert.ThrowsAsync<BillingException>(()=>billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Contacted"),null,default))).Status);
        var contacted=await billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Contacted"),BillingETag.Format(request.Id,1),default);
        Assert.Equal("Contacted",contacted.Status);Assert.Equal(2,contacted.Revision);
        Assert.Equal(403,(await Assert.ThrowsAsync<BillingException>(()=>billing.UpdateEnterpriseStatus(BillingDatabase.Owner,BillingDatabase.Owner,request.Id,new("Rejected"),BillingETag.Format(request.Id,2),default))).Status);
        var package=await billing.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("ENT","Enterprise",1000,12,true,null,100,0),null,default);
        var quote=await billing.CreateEnterpriseQuotation(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new([new(BillingDatabase.Building,package.Id,"New")]),"ent-quote",default);
        Assert.Equal(BillingDatabase.Org,quote.OrganizationId);Assert.Equal("Draft",quote.Status);
        Assert.Equal(quote.Id,(await billing.CreateEnterpriseQuotation(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new([new(BillingDatabase.Building,package.Id,"New")]),"ent-quote",default)).Id);
        var quoted=await billing.GetEnterpriseRequest(BillingDatabase.Admin,request.Id,default);Assert.Equal("Quoted",quoted.Status);Assert.Equal(quote.Id,quoted.QuotationId);
        Assert.Equal(409,(await Assert.ThrowsAsync<BillingException>(()=>billing.CreateEnterpriseQuotation(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new([new(BillingDatabase.Building,package.Id,"New")]),"ent-quote-2",default))).Status);
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payos_payment_requests"));
        Assert.Equal(BillingDatabase.Owner,await db.Scalar($"SELECT requested_by FROM quotations WHERE id='{quote.Id}'"));
    }

    private sealed class FakeEmail:IEmailService
    {
        public List<string> Sent { get; }=[];
        public bool Fail { get; set; }
        public Task SendAsync(string to,string subject,string htmlBody,CancellationToken ct=default)
        {if(Fail)throw new InvalidOperationException("provider down");Sent.Add(to);return Task.CompletedTask;}
    }

    [BillingPostgresFact]
    public async Task Expiry_reminder_is_unique_per_period_and_channel_and_suppressed_by_renewal()
    {
        await using var db=await BillingDatabase.Create();var (factory,entitlement,_)=await PaidEntitlement(db);using var __=factory;
        await db.Sql($"ALTER TABLE service_entitlements DISABLE TRIGGER USER; UPDATE service_entitlements SET starts_at=now()-interval '170 days',ends_at=now()+interval '3 days' WHERE id='{entitlement}'; ALTER TABLE service_entitlements ENABLE TRIGGER USER");
        var email=new FakeEmail{Fail=true};var options=Options.Create(new BillingReminderOptions{Enabled=true,LeadDays=5,MaxAttempts=2});
        await using var context=db.Context();var reminders=new BillingReminders(context,email,options,NullLogger<BillingReminders>.Instance);
        Assert.Equal(1,await reminders.ScheduleAsync(DateTime.UtcNow,default));
        Assert.Equal(0,await reminders.ScheduleAsync(DateTime.UtcNow,default));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM notification_deliveries WHERE channel='Web' AND status='Sent'"));
        Assert.Equal(0,await reminders.DispatchAsync(default));
        Assert.Equal("Pending",await db.Scalar("SELECT status FROM notification_deliveries WHERE channel='Email'"));
        email.Fail=false;Assert.Equal(1,await reminders.DispatchAsync(default));
        Assert.Equal(["owner@example.test"],email.Sent);Assert.Equal(0,await reminders.DispatchAsync(default));
        // A second period or a renewal already in place is not reminded; a pending email is suppressed after renewal.
        await db.Sql("DELETE FROM notification_deliveries; DELETE FROM organization_notifications");
        Assert.Equal(1,await reminders.ScheduleAsync(DateTime.UtcNow,default));
        await db.Sql($"""
            ALTER TABLE service_entitlements DISABLE TRIGGER USER;
            INSERT INTO service_entitlements(id,organization_id,building_id,service_package_id,quotation_id,quotation_item_id,payment_transaction_id,provisioning_key,status,starts_at,ends_at,created_by,price_snapshot,terms_snapshot,created_at,playtest_units_granted,playtest_units_used,commercial_version,learner_limit)
            SELECT gen_random_uuid(),organization_id,building_id,service_package_id,quotation_id,NULL,payment_transaction_id,'renewal-fixture','Suspended',ends_at,ends_at+interval '6 months',created_by,jsonb_build_object(),jsonb_build_object(),now(),0,0,commercial_version,learner_limit FROM service_entitlements WHERE id='{entitlement}';
            ALTER TABLE service_entitlements ENABLE TRIGGER USER;
            """);
        Assert.Equal(0,await reminders.DispatchAsync(default));
        Assert.Equal("SUPPRESSED_RENEWED",await db.Scalar("SELECT last_error FROM notification_deliveries WHERE channel='Email'"));
        await db.Sql("DELETE FROM notification_deliveries; DELETE FROM organization_notifications");
        Assert.Equal(0,await reminders.ScheduleAsync(DateTime.UtcNow,default));
    }
}
