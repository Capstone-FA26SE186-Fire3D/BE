using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingQuotationCancelTests
{
    private static HttpRequestMessage Cancel(Guid id,long revision,string key,string? reason="Customer changed plan",bool admin=false,string? etag=null)
    {
        var request=new HttpRequestMessage(HttpMethod.Post,admin?$"/api/admin/quotations/{id}/cancel":$"/api/billing/quotations/{id}/cancel"){Content=JsonContent.Create(new {reason})};
        request.Headers.TryAddWithoutValidation("If-Match",etag??BillingETag.Format(id,revision));request.Headers.Add("Idempotency-Key",key);return request;
    }
    private static async Task<QuotationResponse> Draft(BillingDatabase db)
    {
        await using var context=db.Context();var billing=new BillingService(context);
        var package=await billing.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("P"+Guid.NewGuid().ToString("N"),"Test",2000,6,true,null,20,0),null,default);
        return await billing.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new QuotationItemRequest(BillingDatabase.Building,package.Id,"New")]),Guid.NewGuid().ToString("N"),default);
    }
    private static async Task<long> Revision(BillingDatabase db,Guid id)=>(long)(await db.Scalar($"SELECT revision FROM quotations WHERE id='{id}'"))!;

    [BillingPostgresFact]
    public async Task Draft_cancel_requires_headers_reason_and_scope_and_replays_without_a_second_audit()
    {
        await using var db=await BillingDatabase.Create();using var factory=new BillingApiTests.Factory(db);
        using var owner=factory.As(BillingDatabase.Owner);using var other=factory.As(BillingDatabase.Other);using var admin=factory.As(BillingDatabase.Admin);
        var draft=await Draft(db);
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.SendAsync(Cancel(draft.Id,draft.Revision,"no-reason",reason:"   "))).StatusCode);
        var missing=new HttpRequestMessage(HttpMethod.Post,$"/api/billing/quotations/{draft.Id}/cancel"){Content=JsonContent.Create(new {reason="x"})};missing.Headers.Add("Idempotency-Key","no-match");
        Assert.Equal((HttpStatusCode)428,(await owner.SendAsync(missing)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await other.SendAsync(Cancel(draft.Id,draft.Revision,"foreign"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.SendAsync(Cancel(draft.Id,draft.Revision,"owner-admin-route",admin:true))).StatusCode);
        var cancelled=await owner.SendAsync(Cancel(draft.Id,draft.Revision,"cancel-1"));
        Assert.Equal(HttpStatusCode.OK,cancelled.StatusCode);
        Assert.Equal("Cancelled",(await PayosCheckoutTests.Json(cancelled)).GetProperty("status").GetString());
        // Same key and input replays even with the now stale ETag; another reason under that key is a conflict.
        Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(Cancel(draft.Id,draft.Revision,"cancel-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await owner.SendAsync(Cancel(draft.Id,draft.Revision,"cancel-1",reason:"Other reason"))).StatusCode);
        // A new key on a Cancelled quotation returns the current state without another audit row.
        Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(Cancel(draft.Id,await Revision(db,draft.Id),"cancel-2"))).StatusCode);
        Assert.Equal(1L,await db.Scalar($"SELECT count(*) FROM audit_logs WHERE target_id='{draft.Id}' AND new_values->>'status'='Cancelled'"));
        Assert.Equal("Customer changed plan",(string?)await db.Scalar($"SELECT new_values->>'reason' FROM audit_logs WHERE target_id='{draft.Id}' AND new_values->>'status'='Cancelled'"));
        // Admin route cancels another tenant's Issued quotation; an Expired quotation keeps its status.
        var issued=await Draft(db);
        await using(var context=db.Context())
            issued=await new BillingService(context).IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,issued.Id,new(0,"Terms",DateTimeOffset.UtcNow.AddDays(1),issued.Items.Select(x=>new QuotationPeriodRequest(x.Id,new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(2),TimeSpan.Zero))).ToArray()),BillingETag.Format(issued.Id,issued.Revision),default);
        Assert.Equal(HttpStatusCode.OK,(await admin.SendAsync(Cancel(issued.Id,issued.Revision,"admin-cancel",admin:true))).StatusCode);
        var expiring=await PayosCheckoutTests.Accepted(db);
        await db.Sql($"UPDATE quotations SET status='Expired' WHERE id='{expiring}'");
        var expired=await owner.SendAsync(Cancel(expiring,await Revision(db,expiring),"expired"));
        Assert.Equal(HttpStatusCode.Conflict,expired.StatusCode);Assert.Contains("QUOTATION_EXPIRED",await expired.Content.ReadAsStringAsync());
        // No job writes Expired: an Accepted quotation past validUntil is already expired for cancellation.
        var lapsed=await PayosCheckoutTests.Accepted(db);
        await db.Sql($"ALTER TABLE quotations DISABLE TRIGGER USER; UPDATE quotations SET valid_until=now()-interval '1 minute' WHERE id='{lapsed}'; ALTER TABLE quotations ENABLE TRIGGER USER");
        var late=await owner.SendAsync(Cancel(lapsed,await Revision(db,lapsed),"lapsed"));
        Assert.Equal(HttpStatusCode.Conflict,late.StatusCode);Assert.Contains("QUOTATION_EXPIRED",await late.Content.ReadAsStringAsync());
        Assert.Equal("Accepted",(string?)await db.Scalar($"SELECT status::text FROM quotations WHERE id='{lapsed}'"));
    }

    [BillingPostgresFact]
    public async Task Cancel_waits_for_checkout_resolution_refuses_paid_quotations_and_blocks_new_links()
    {
        await using var db=await BillingDatabase.Create();var provider=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);using var anonymous=factory.CreateClient();
        var quote=await PayosCheckoutTests.Accepted(db);
        var checkout=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote)));
        var blocked=await owner.SendAsync(Cancel(quote,await Revision(db,quote),"open-checkout"));
        Assert.Equal(HttpStatusCode.Conflict,blocked.StatusCode);
        var problem=JsonDocument.Parse(await blocked.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("QUOTATION_CHECKOUT_UNRESOLVED",problem.GetProperty("code").GetString());
        Assert.Equal(checkout.GetProperty("checkoutId").GetGuid(),problem.GetProperty("checkoutIds")[0].GetGuid());
        // After the provider confirms the checkout cancellation the quotation can be cancelled, and no new link can be made for it.
        using(var cancelLink=new HttpRequestMessage(HttpMethod.Post,$"/api/payments/payos/requests/{checkout.GetProperty("paymentRequestId").GetGuid()}/cancel"))
        {cancelLink.Headers.Add("Idempotency-Key","link-cancel");Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(cancelLink)).StatusCode);}
        Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(Cancel(quote,await Revision(db,quote),"after-resolution"))).StatusCode);
        Assert.False((await owner.SendAsync(PayosCheckoutTests.Create(quote,"checkout-after-cancel"))).IsSuccessStatusCode);
        Assert.Equal(0L,await db.Scalar($"SELECT count(*) FROM billing_checkout_operations WHERE quotation_id='{quote}' AND status IN('Creating','Ready','NeedsReconcile')"));

        // Money that arrives late for the cancelled quotation is kept; provisioning waits for reconciliation and nothing is granted.
        var order=checkout.GetProperty("orderCode").GetInt64();
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(order,12000,"VND","link"+order,"late-after-cancel","2026-10-02 18:00:00"))).StatusCode);
        await PayosWebhookTests.Recover(factory,db);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements WHERE payment_transaction_id IS NOT NULL"));
        Assert.Equal("PAYOS_QUOTATION_CANCELLED",(string?)await db.Scalar($"SELECT last_error FROM payment_provisioning_records WHERE quotation_id='{quote}' LIMIT 1"));
        Assert.Equal("NeedsReconcile",(string?)await db.Scalar($"SELECT status FROM payment_provisioning_records WHERE quotation_id='{quote}' LIMIT 1"));
        Assert.Equal("Cancelled",(string?)await db.Scalar($"SELECT status::text FROM quotations WHERE id='{quote}'"));

        // A paid quotation cannot be cancelled.
        var second=Guid.NewGuid();
        await db.Sql($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{second}','{BillingDatabase.Org}','Second',true,'{BillingDatabase.Owner}',now(),now()); INSERT INTO building_locations(building_id,address,created_at,updated_at) VALUES('{second}','2 Example Street',now(),now())");
        var paid=await PayosCheckoutTests.Accepted(db,"New",[second]);
        var paidCheckout=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(paid,"paid-checkout")));
        var paidOrder=paidCheckout.GetProperty("orderCode").GetInt64();
        await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(paidOrder,12000,"VND","link"+paidOrder,"paid-reference","2026-10-02 18:00:00"));
        await PayosWebhookTests.Recover(factory,db);
        var refused=await owner.SendAsync(Cancel(paid,await Revision(db,paid),"paid"));
        Assert.Equal(HttpStatusCode.Conflict,refused.StatusCode);Assert.Contains("QUOTATION_ALREADY_PAID",await refused.Content.ReadAsStringAsync());
    }

    [BillingPostgresFact]
    public async Task Cancel_and_checkout_creation_race_never_leaves_a_link_on_a_cancelled_quotation()
    {
        await using var db=await BillingDatabase.Create();var provider=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);using var owner2=factory.As(BillingDatabase.Owner);
        var quote=await PayosCheckoutTests.Accepted(db);var revision=await Revision(db,quote);
        var results=await Task.WhenAll(owner.SendAsync(Cancel(quote,revision,"race-cancel")),owner2.SendAsync(PayosCheckoutTests.Create(quote,"race-checkout")));
        var status=(string?)await db.Scalar($"SELECT status::text FROM quotations WHERE id='{quote}'");
        var open=(long)(await db.Scalar($"SELECT count(*) FROM billing_checkout_operations WHERE quotation_id='{quote}' AND status IN('Creating','Ready','NeedsReconcile')"))!;
        if(status=="Cancelled"){Assert.Equal(HttpStatusCode.OK,results[0].StatusCode);Assert.Equal(0L,open);}
        else{Assert.Equal(HttpStatusCode.Conflict,results[0].StatusCode);Assert.Equal(1L,open);}
    }
}
