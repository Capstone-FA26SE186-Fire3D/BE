using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class BillingV7PayosTests
{
    private static async Task<Guid> Accepted(BillingDatabase database,string code,string key,int quota=100)
    {
        await using var db=database.Context();var service=new BillingService(db);
        var policy=await service.CreateQuotaPolicy(BillingDatabase.Admin,BillingDatabase.Admin,new("tokens",DateTimeOffset.UtcNow.AddDays(-1)),default);
        var package=await service.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new(code,code,2000,6,true,null,25,quota,policy.Id),null,default);
        var quote=await service.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,package.Id,"New")]),key,default);
        var start=new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2),TimeSpan.Zero);
        quote=await service.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"Terms",DateTimeOffset.UtcNow.AddDays(1),[new(quote.Items[0].Id,start)]),BillingETag.Format(quote.Id,quote.Revision),default);
        await service.AcceptQuotation(BillingDatabase.Owner,BillingDatabase.Owner,quote.Id,BillingETag.Format(quote.Id,quote.Revision),default);
        return quote.Id;
    }
    [BillingPostgresFact]
    public async Task Concurrent_quotations_cannot_create_overlapping_provider_links_and_cancel_releases_only_confirmed_reservation()
    {
        await using var database=await BillingDatabase.Create();var one=await Accepted(database,"ONE","one");var two=await Accepted(database,"TWO","two");
        var provider=new FakePayos();using var factory=new BillingApiTests.Factory(database,provider);using var owner=factory.As(BillingDatabase.Owner);
        var responses=await Task.WhenAll(owner.SendAsync(PayosCheckoutTests.Create(one,"one")),owner.SendAsync(PayosCheckoutTests.Create(two,"two")));
        Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.Created);Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.Conflict);
        Assert.Equal(1,provider.Creates);Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM billing_service_reservations WHERE status='Reserved'"));
        var success=await PayosCheckoutTests.Json(responses.Single(x=>x.IsSuccessStatusCode));
        using var cancel=new HttpRequestMessage(HttpMethod.Post,$"/api/payments/payos/requests/{success.GetProperty("paymentRequestId").GetGuid()}/cancel");
        cancel.Headers.Add("Idempotency-Key","cancel");Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(cancel)).StatusCode);
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM billing_service_reservations WHERE status='Released'"));
        var loser=responses[0].StatusCode==HttpStatusCode.Conflict?one:two;
        Assert.Equal(HttpStatusCode.Created,(await owner.SendAsync(PayosCheckoutTests.Create(loser,"after-cancel"))).StatusCode);
        Assert.Equal(2,provider.Creates);
    }
    [BillingPostgresFact]
    public async Task Payment_provisions_fixed_period_seats_and_quota_once_with_atomic_rollback()
    {
        await using var database=await BillingDatabase.Create();var quote=await Accepted(database,"ONE","one");
        using var factory=new BillingApiTests.Factory(database,new FakePayos());
        var checkout=await PayosProvisioningTests.Paid(factory,quote,"paid");
        await database.Sql("CREATE FUNCTION fail_quota_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.target_entity='billing_ai_quota_grants' THEN RAISE EXCEPTION 'test quota rollback'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_quota_audit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_quota_audit()");
        await PayosWebhookTests.Recover(factory,database);
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM service_entitlements"));Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM billing_ai_quota_grants"));
        Assert.Equal("Paid",await database.Scalar("SELECT status::text FROM payos_payment_requests"));
        await database.Sql("DROP TRIGGER fail_quota_audit ON audit_logs; UPDATE payment_provisioning_records SET next_attempt_at=now()");
        await Task.WhenAll(PayosWebhookTests.Recover(factory,database),PayosWebhookTests.Recover(factory,database));
        Assert.True(Equals(1L,await database.Scalar("SELECT count(*) FROM service_entitlements WHERE commercial_version=7 AND learner_limit=25")),
            "Provisioning failed: "+await database.Scalar("SELECT last_error FROM payment_provisioning_records"));
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM billing_ai_quota_grants WHERE quota_units=100 AND rollover='None'"));
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM service_entitlements e JOIN quotation_building_items i ON i.id=e.quotation_item_id WHERE e.starts_at=i.starts_at AND e.ends_at=i.ends_at"));
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM billing_service_reservations WHERE status='Consumed'"));
        Assert.Equal(false,await database.Scalar("SELECT has_table_privilege('fire3d_api','billing_ai_quota_grants','INSERT')"));
        await PayosWebhookTests.Recover(factory,database);Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM billing_ai_quota_grants"));
    }
    [BillingPostgresFact]
    public async Task Timeout_retains_reservation_and_late_paid_after_cancel_remains_money_without_provisioning()
    {
        await using var database=await BillingDatabase.Create();var quote=await Accepted(database,"ONE","one",0);
        var provider=new FakePayos{LoseCreateResponse=true};using var factory=new BillingApiTests.Factory(database,provider);using var owner=factory.As(BillingDatabase.Owner);
        var result=await owner.SendAsync(PayosCheckoutTests.Create(quote,"one"));Assert.Equal(HttpStatusCode.Accepted,result.StatusCode);
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM billing_service_reservations WHERE status='Reserved'"));
        await database.Sql("UPDATE billing_checkout_operations SET next_attempt_at=now()");
        await PayosWebhookTests.Recover(factory,database);
        var state=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote,"one")));
        using var cancel=new HttpRequestMessage(HttpMethod.Post,$"/api/payments/payos/requests/{state.GetProperty("paymentRequestId").GetGuid()}/cancel");
        cancel.Headers.Add("Idempotency-Key","cancel");Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(cancel)).StatusCode);
        using var webhook=factory.CreateClient();var order=state.GetProperty("orderCode").GetInt64();
        Assert.Equal(HttpStatusCode.OK,(await webhook.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(order,12000,"VND","link"+order,"late","2026-10-08 12:00:00"))).StatusCode);
        await PayosWebhookTests.Recover(factory,database);
        Assert.Equal("Paid",await database.Scalar("SELECT status::text FROM payos_payment_requests"));
        Assert.Equal("Applied",await database.Scalar("SELECT status::text FROM payment_transactions"));
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal("NeedsReconcile",await database.Scalar("SELECT status FROM payment_provisioning_records"));
    }
}
