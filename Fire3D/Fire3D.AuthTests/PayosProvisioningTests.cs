using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Billing;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class PayosProvisioningTests
{
    [BillingPostgresFact]
    public async Task Admin_can_recover_expired_pending_inbox_and_provisioning_claims_at_attempt_limit()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);
        using var factory=new BillingApiTests.Factory(db,new FakePayos());using var admin=factory.As(BillingDatabase.Admin);
        var checkout=await Paid(factory,quote,"interrupted");var id=checkout.GetProperty("checkoutId").GetGuid();
        // Durable state left by a crash after the tenth claim, before applying the inbox.
        await db.Sql("UPDATE payos_webhook_inbox SET status='Pending',attempts=10,lease_token=gen_random_uuid(),lease_until=now()-interval '1 second'");
        await db.Sql("CREATE FUNCTION fail_recovery_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.target_entity='service_entitlements' THEN RAISE EXCEPTION 'test interrupted provisioning'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_recovery BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_recovery_audit()");
        Assert.Equal(HttpStatusCode.Accepted,(await admin.PostAsync($"/api/admin/payments/payos/checkouts/{id}/reconcile",null)).StatusCode);
        await PayosWebhookTests.Recover(factory,db);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        // Simulate the same interruption on the provisioning queue. Never redo Applied money.
        await db.Sql("DROP TRIGGER fail_recovery ON audit_logs; UPDATE payment_provisioning_records SET status='Pending',attempts=10,lease_token=gen_random_uuid(),lease_until=now()-interval '1 second'");
        Assert.Equal(HttpStatusCode.Accepted,(await admin.PostAsync($"/api/admin/payments/payos/checkouts/{id}/reconcile",null)).StatusCode);
        await PayosWebhookTests.Recover(factory,db);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_provisioning_records WHERE status='Succeeded'"));
    }

    [Theory]
    [InlineData("2026-01-31T23:30:00Z",null,"2026-02-28T23:30:00Z")]
    [InlineData("2024-01-31T23:30:00Z",null,"2024-02-29T23:30:00Z")]
    [InlineData("2026-01-31T23:30:00Z","2025-12-01T00:00:00Z","2026-02-28T23:30:00Z")]
    [InlineData("2026-02-01T06:30:00+07:00","2026-03-31T23:30:00Z","2026-04-30T23:30:00Z")]
    public void Calendar_months_are_computed_in_UTC_and_early_renewal_starts_after_purchased_period(string activation,string? previous,string expected)
    {
        var (start,end)=Fire3D.Infrastructure.Billing.PayosPayments.Period(DateTimeOffset.Parse(activation),previous is null?null:DateTimeOffset.Parse(previous),1);
        Assert.Equal(DateTimeKind.Utc,start.Kind);Assert.Equal(DateTimeOffset.Parse(expected).UtcDateTime,end);
    }
    [BillingPostgresFact]
    public async Task Competing_fixed_renewals_allow_one_checkout_then_a_new_quotation_forms_a_contiguous_period()
    {
        await using var db=await BillingDatabase.Create();using var factory=new BillingApiTests.Factory(db,new FakePayos());
        var first=await PayosCheckoutTests.Accepted(db);await Paid(factory,first,"first");await PayosWebhookTests.Recover(factory,db);
        var a=await PayosCheckoutTests.Accepted(db,"Renewal");var b=await PayosCheckoutTests.Accepted(db,"Renewal");
        using var owner=factory.As(BillingDatabase.Owner);using var webhook=factory.CreateClient();
        var results=await Task.WhenAll(owner.SendAsync(PayosCheckoutTests.Create(a,"renew-a")),owner.SendAsync(PayosCheckoutTests.Create(b,"renew-b")));
        Assert.Single(results,x=>x.StatusCode==HttpStatusCode.Created);Assert.Single(results,x=>x.StatusCode==HttpStatusCode.Conflict);
        var winner=await PayosCheckoutTests.Json(results.Single(x=>x.IsSuccessStatusCode));var order=winner.GetProperty("orderCode").GetInt64();
        Assert.Equal(HttpStatusCode.OK,(await webhook.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(order,12000,"VND","link"+order,"renew-winner","2026-10-02 18:00:00"))).StatusCode);
        await Task.WhenAll(PayosWebhookTests.Recover(factory,db),PayosWebhookTests.Recover(factory,db));
        var next=await PayosCheckoutTests.Accepted(db,"Renewal");await Paid(factory,next,"next");await PayosWebhookTests.Recover(factory,db);
        Assert.Equal(3L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM (SELECT starts_at,lag(ends_at) OVER(ORDER BY starts_at) previous_end FROM service_entitlements) periods WHERE previous_end IS NOT NULL AND starts_at<>previous_end"));
        Assert.Equal(3L,await db.Scalar("SELECT count(*) FROM payment_provisioning_records WHERE status='Succeeded'"));
        var missingLease=await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Sql("SET ROLE fet3d_payos_webhook_executor; SELECT claim_payos_provisioning_context((SELECT NULL::uuid),NULL)"));
        Assert.Equal("PAYOS_STALE_LEASE",missingLease.MessageText);
    }
    internal static async Task<System.Text.Json.JsonElement> Paid(BillingApiTests.Factory factory,Guid quote,string key)
    {
        using var owner=factory.As(BillingDatabase.Owner);using var webhook=factory.CreateClient();
        var result=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote,key)));var order=result.GetProperty("orderCode").GetInt64();
        Assert.Equal(HttpStatusCode.OK,(await webhook.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(order,(long)result.GetProperty("amount").GetDecimal(),"VND","link"+order,key,"2026-10-02 18:00:00"))).StatusCode);return result;
    }
    [BillingPostgresFact]
    public async Task Partial_provisioning_recovers_only_failed_line_and_renewal_adds_a_new_period()
    {
        await using var db=await BillingDatabase.Create();var second=Guid.NewGuid();
        await db.Sql($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{second}','{BillingDatabase.Org}','Second',true,'{BillingDatabase.Owner}',now(),now()); INSERT INTO building_locations(building_id,address,created_at,updated_at) VALUES('{second}','Second address',now(),now())");
        var quote=await PayosCheckoutTests.Accepted(db,buildings:[BillingDatabase.Building,second]);
        using var factory=new BillingApiTests.Factory(db,new FakePayos());using var owner=factory.As(BillingDatabase.Owner);using var other=factory.As(BillingDatabase.Other);using var admin=factory.As(BillingDatabase.Admin);
        var checkout=await Paid(factory,quote,"multi");await db.Sql($"UPDATE buildings SET is_active=false WHERE id='{second}'");
        await PayosWebhookTests.Recover(factory,db);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        var requestId=checkout.GetProperty("paymentRequestId").GetGuid();
        var response=await PayosCheckoutTests.Json(await owner.GetAsync($"/api/payments/payos/requests/{requestId}"));
        Assert.Equal("Paid",response.GetProperty("paymentStatus").GetString());Assert.Equal("NeedsReconcile",response.GetProperty("provisioningStatus").GetString());
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/payments/payos/requests/{requestId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.PostAsync($"/api/admin/payments/payos/checkouts/{checkout.GetProperty("checkoutId").GetGuid()}/reconcile",null)).StatusCode);
        await db.Sql($"UPDATE buildings SET is_active=true WHERE id='{second}'");
        Assert.Equal(HttpStatusCode.Accepted,(await admin.PostAsync($"/api/admin/payments/payos/checkouts/{checkout.GetProperty("checkoutId").GetGuid()}/reconcile",null)).StatusCode);
        await PayosWebhookTests.Recover(factory,db);
        Assert.Equal(2L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(1L,await db.Scalar("SELECT count(DISTINCT starts_at) FROM service_entitlements"));
        var last=(DateTime)(await db.Scalar($"SELECT ends_at FROM service_entitlements WHERE building_id='{BillingDatabase.Building}'"))!;
        var renewal=await PayosCheckoutTests.Accepted(db,"Renewal");await Paid(factory,renewal,"renew");await PayosWebhookTests.Recover(factory,db);
        Assert.Equal(3L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(last,await db.Scalar($"SELECT starts_at FROM service_entitlements WHERE building_id='{BillingDatabase.Building}' ORDER BY starts_at DESC LIMIT 1"));
        Assert.Equal(last.AddMonths(6),await db.Scalar($"SELECT ends_at FROM service_entitlements WHERE building_id='{BillingDatabase.Building}' ORDER BY starts_at DESC LIMIT 1"));
        await PayosWebhookTests.Recover(factory,db);Assert.Equal(3L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        var entitlements=await PayosCheckoutTests.Json(await other.GetAsync("/api/billing/entitlements"));Assert.Equal(0,entitlements.GetProperty("total").GetInt32());
    }
    [BillingPostgresFact]
    public async Task Entitlement_and_line_audit_roll_back_and_stale_executor_cannot_finalize()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);
        using var factory=new BillingApiTests.Factory(db,new FakePayos());var checkout=await Paid(factory,quote,"atomic");
        // Inject only an entitlement audit failure: payment must stay Paid, line must retry.
        await db.Sql("CREATE FUNCTION fail_entitlement_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.target_entity='service_entitlements' THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_entitlement BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_entitlement_audit()");
        await PayosWebhookTests.Recover(factory,db);Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal("Paid",await db.Scalar("SELECT status::text FROM payos_payment_requests"));
        var line=(Guid)(await db.Scalar("SELECT id FROM payment_provisioning_records"))!;
        var error=await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Sql($"SET ROLE fet3d_payos_webhook_executor; SELECT claim_payos_provisioning_context('{line}','{Guid.NewGuid()}')"));
        Assert.Equal("PAYOS_STALE_LEASE",error.MessageText);
        await db.Sql("DROP TRIGGER fail_entitlement ON audit_logs; UPDATE payment_provisioning_records SET next_attempt_at=now()");
        await Task.WhenAll(PayosWebhookTests.Recover(factory,db),PayosWebhookTests.Recover(factory,db));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_provisioning_records WHERE status='Succeeded'"));
    }
}
