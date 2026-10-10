using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Xunit;
namespace Fire3D.AuthTests;

public sealed class PayosCancellationReplayTests
{
    private static HttpRequestMessage Cancel(Guid id,string key="cancel-replay")
    {
        var request=new HttpRequestMessage(HttpMethod.Post,$"/api/payments/payos/requests/{id}/cancel");
        request.Headers.Add("Idempotency-Key",key);return request;
    }
    [BillingPostgresFact]
    public async Task Completed_payment_replays_the_recorded_cancel_without_repeating_side_effects()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);var provider=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);using var webhook=factory.CreateClient();
        var checkout=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote)));
        var id=checkout.GetProperty("paymentRequestId").GetGuid();var order=checkout.GetProperty("orderCode").GetInt64();
        Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(Cancel(id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await webhook.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(order,12000,"VND","link"+order,"late-cancel-replay","2026-10-02 18:00:00"))).StatusCode);
        await PayosWebhookTests.Recover(factory,db);
        var audits=await db.Scalar("SELECT count(*) FROM audit_logs");var gets=provider.Gets;var cancels=provider.Cancels;
        var replay=await owner.SendAsync(Cancel(id));Assert.Equal(HttpStatusCode.OK,replay.StatusCode);
        Assert.Equal("Completed",(await PayosCheckoutTests.Json(replay)).GetProperty("checkoutStatus").GetString());
        Assert.Equal(gets,provider.Gets);Assert.Equal(cancels,provider.Cancels);
        Assert.Equal(audits,await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal("NeedsReconcile",await db.Scalar("SELECT status FROM payment_provisioning_records"));
        var newCancel=await owner.SendAsync(Cancel(id,"new-cancel"));Assert.Equal(HttpStatusCode.Conflict,newCancel.StatusCode);
        Assert.Equal("PAYMENT_ALREADY_PAID",(await newCancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var secondQuote=await PayosCheckoutTests.Accepted(db);
        var second=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(secondQuote,"second")));
        var conflict=await owner.SendAsync(Cancel(second.GetProperty("paymentRequestId").GetGuid()));Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",(await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
    [BillingPostgresFact]
    public async Task Pending_cancel_replay_does_not_call_provider_or_mutate_the_existing_operation()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);var provider=new FakePayos{LoseCancelResponse=true};
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);
        var checkout=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote)));var id=checkout.GetProperty("paymentRequestId").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted,(await owner.SendAsync(Cancel(id))).StatusCode);
        var state=await db.Scalar("SELECT row_to_json(op)::text FROM billing_checkout_operations op");var audits=await db.Scalar("SELECT count(*) FROM audit_logs");
        var gets=provider.Gets;var cancels=provider.Cancels;
        Assert.Equal(HttpStatusCode.Accepted,(await owner.SendAsync(Cancel(id))).StatusCode);
        Assert.Equal(gets,provider.Gets);Assert.Equal(cancels,provider.Cancels);
        Assert.Equal(state,await db.Scalar("SELECT row_to_json(op)::text FROM billing_checkout_operations op"));
        Assert.Equal(audits,await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payment_transactions"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        await db.Sql("UPDATE billing_checkout_operations SET next_attempt_at=now()-interval '5 seconds'");await PayosWebhookTests.Recover(factory,db);
        var terminal=await owner.SendAsync(Cancel(id));Assert.Equal(HttpStatusCode.OK,terminal.StatusCode);
        Assert.Equal("Cancelled",(await PayosCheckoutTests.Json(terminal)).GetProperty("checkoutStatus").GetString());
    }
}
