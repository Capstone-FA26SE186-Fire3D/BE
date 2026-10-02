using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class PayosWebhookTests
{
    [BillingPostgresFact]
    public async Task Audit_failure_rolls_back_ledger_and_parallel_retries_apply_once()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);var fake=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,fake);using var owner=factory.As(BillingDatabase.Owner);using var anonymous=factory.CreateClient();
        var created=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote)));var order=created.GetProperty("orderCode").GetInt64();
        var ev=new VerifiedPayosEvent(order,2000,"VND","link"+order,"rollback-ref","2026-10-02 18:00:00");
        foreach(var mismatch in new[]{ev with {Currency="USD",Reference="usd"},ev with {PaymentLinkId="wrong",Reference="link-mismatch"}})
            Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",mismatch)).StatusCode);
        await Recover(factory,db);Assert.Equal(2L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Rejected'"));
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",ev)).StatusCode);
        await db.Sql("CREATE FUNCTION fail_payos_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test audit rollback'; END $$; CREATE TRIGGER fail_payos BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_payos_audit()");
        await Recover(factory,db);Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payment_provisioning_records"));
        await db.Sql("DROP TRIGGER fail_payos ON audit_logs");await Task.WhenAll(Recover(factory,db),Recover(factory,db));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_provisioning_records"));
        Assert.Equal(false,await db.Scalar("SELECT has_function_privilege('fet3d_payos_webhook_executor','apply_verified_payos_webhook(uuid,text,text,bigint,numeric,text,jsonb)','EXECUTE')"));
    }
    internal static async Task Recover(BillingApiTests.Factory factory,BillingDatabase db)
    {
        await db.Sql("UPDATE payos_webhook_inbox SET next_attempt_at=now()");
        using var scope=factory.Services.CreateScope();await scope.ServiceProvider.GetRequiredService<IPayosPayments>().Recover(default);
    }
    [BillingPostgresFact]
    public async Task Verified_inbox_replays_and_rejects_mismatch_without_entitlements()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);var fake=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,fake);using var owner=factory.As(BillingDatabase.Owner);using var anonymous=factory.CreateClient();
        var created=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote)));var order=created.GetProperty("orderCode").GetInt64();
        var ev=new VerifiedPayosEvent(order,2000,"VND","link"+order,"bank-ref-1","2026-10-02 18:00:00");
        Assert.Equal(HttpStatusCode.BadRequest,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",new{invalidSignature=true})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",ev with {Amount=1999,Reference="wrong-amount"})).StatusCode);
        await Recover(factory,db);Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",ev)).StatusCode);
        await Recover(factory,db);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_provisioning_records"));
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",ev)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",ev with {Amount=3000})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",ev with {Reference="another-reference"})).StatusCode);
        await Recover(factory,db);Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payos_payment_requests WHERE status='Paid'"));
    }
    [BillingPostgresFact]
    public async Task Early_and_late_webhooks_are_retained_and_paid_after_confirmed_cancel()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);var fake=new FakePayos{LoseCreateResponse=true};
        using var factory=new BillingApiTests.Factory(db,fake);using var owner=factory.As(BillingDatabase.Owner);using var anonymous=factory.CreateClient();
        var created=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote)));var order=created.GetProperty("orderCode").GetInt64();
        var ev=new VerifiedPayosEvent(order,2000,"VND","link"+order,"early-reference","2026-10-02 18:00:00");
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",ev)).StatusCode);
        await Recover(factory,db);Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payment_transactions"));
        await db.Sql("UPDATE billing_checkout_operations SET next_attempt_at=now()");await Recover(factory,db);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_checkout_operations WHERE status='Completed'"));
        var quote2=await PayosCheckoutTests.Accepted(db,"Renewal");var second=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote2,"quote2")));
        var id=second.GetProperty("paymentRequestId").GetGuid();using var cancel=new HttpRequestMessage(HttpMethod.Post,$"/api/payments/payos/requests/{id}/cancel");cancel.Headers.Add("Idempotency-Key","cancel2");
        Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(cancel)).StatusCode);
        var order2=second.GetProperty("orderCode").GetInt64();
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",ev with {OrderCode=order2,PaymentLinkId="link"+order2,Reference="late-reference"})).StatusCode);
        await Recover(factory,db);Assert.Equal(2L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
    }
}
