using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingPaymentLedgerTests
{
    private static async Task<JsonElement> Get(HttpClient client,string url)
    {
        var response=await client.GetAsync(url);var text=await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,text);return JsonDocument.Parse(text).RootElement.Clone();
    }

    [BillingPostgresFact]
    public async Task Checkout_and_payment_lists_link_quotation_checkout_payment_and_provisioning_without_duplicates()
    {
        await using var db=await BillingDatabase.Create();var provider=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);using var other=factory.As(BillingDatabase.Other);
        using var admin=factory.As(BillingDatabase.Admin);using var anonymous=factory.CreateClient();
        var second=Guid.NewGuid();
        await db.Sql($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{second}','{BillingDatabase.Org}','Second',true,'{BillingDatabase.Owner}',now(),now()); INSERT INTO building_locations(building_id,address,created_at,updated_at) VALUES('{second}','2 Example Street',now(),now())");

        // A checkout whose provider response was lost has no payment request yet; it still appears in the checkout list.
        var lost=await PayosCheckoutTests.Accepted(db,"New",[second]);
        provider.LoseCreateResponse=true;
        Assert.Equal(HttpStatusCode.Accepted,(await owner.SendAsync(PayosCheckoutTests.Create(lost,"lost"))).StatusCode);
        provider.LoseCreateResponse=false;
        var lostRow=Assert.Single((await Get(admin,$"/api/admin/payments/payos/checkouts?quotationId={lost}")).GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null,lostRow.GetProperty("paymentRequestId").ValueKind);
        Assert.Equal("NotStarted",lostRow.GetProperty("provisioningStatus").GetString());

        // Two-Building quotation: one payment row however many provisioning lines exist.
        var third=Guid.NewGuid();
        await db.Sql($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{third}','{BillingDatabase.Org}','Third',true,'{BillingDatabase.Owner}',now(),now()); INSERT INTO building_locations(building_id,address,created_at,updated_at) VALUES('{third}','3 Example Street',now(),now())");
        var multi=await PayosCheckoutTests.Accepted(db,"New",[BillingDatabase.Building,third]);
        var checkout=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(multi,"multi")));
        var pending=Assert.Single((await Get(owner,$"/api/billing/quotations/{multi}/checkouts")).GetProperty("items").EnumerateArray());
        Assert.Equal("Ready",pending.GetProperty("checkoutStatus").GetString());
        Assert.Equal("Pending",pending.GetProperty("paymentStatus").GetString());
        Assert.False(pending.TryGetProperty("checkoutUrl",out _));
        var order=checkout.GetProperty("orderCode").GetInt64();
        Assert.Equal(HttpStatusCode.OK,(await anonymous.PostAsJsonAsync("/api/payments/payos/webhook",new VerifiedPayosEvent(order,(long)checkout.GetProperty("amount").GetDecimal(),"VND","link"+order,"multi-ref","2026-10-02 18:00:00"))).StatusCode);
        await PayosWebhookTests.Recover(factory,db);
        var paid=Assert.Single((await Get(owner,$"/api/billing/quotations/{multi}/checkouts")).GetProperty("items").EnumerateArray());
        Assert.Equal("Paid",paid.GetProperty("paymentStatus").GetString());
        Assert.Equal("Applied",paid.GetProperty("transactionStatus").GetString());
        Assert.Equal("Succeeded",paid.GetProperty("provisioningStatus").GetString());

        // Paid is never shown as fully provisioned while a line waits or needs reconciliation.
        Assert.Equal(2L,await db.Scalar($"SELECT count(*) FROM payment_provisioning_records WHERE quotation_id='{multi}'"));
        await db.Sql($"UPDATE payment_provisioning_records SET status='Pending' WHERE id=(SELECT id FROM payment_provisioning_records WHERE quotation_id='{multi}' ORDER BY id LIMIT 1)");
        var partial=(await Get(admin,$"/api/admin/payments/payos/requests?quotationId={multi}"));
        Assert.Equal(1,partial.GetProperty("total").GetInt32());
        Assert.Equal("Partial",Assert.Single(partial.GetProperty("items").EnumerateArray()).GetProperty("provisioningStatus").GetString());
        await db.Sql($"UPDATE payment_provisioning_records SET status='NeedsReconcile',last_error='FIXTURE' WHERE quotation_id='{multi}' AND status='Pending'");
        Assert.Equal("NeedsReconcile",(await Get(admin,$"/api/admin/payments/payos/requests?quotationId={multi}")).GetProperty("items")[0].GetProperty("provisioningStatus").GetString());

        // Filters, stable paging and scope.
        var all=await Get(admin,"/api/admin/payments/payos/checkouts?pageSize=1");
        Assert.Equal(2,all.GetProperty("total").GetInt32());
        var first=all.GetProperty("items")[0].GetProperty("checkoutId").GetGuid();
        var page2=(await Get(admin,"/api/admin/payments/payos/checkouts?pageSize=1&page=2")).GetProperty("items")[0].GetProperty("checkoutId").GetGuid();
        Assert.NotEqual(first,page2);
        Assert.Equal(1,(await Get(admin,"/api/admin/payments/payos/checkouts?status=NeedsReconcile")).GetProperty("total").GetInt32());
        Assert.Equal(0,(await Get(admin,$"/api/admin/payments/payos/requests?organizationId={BillingDatabase.OtherOrg}")).GetProperty("total").GetInt32());
        Assert.Equal(1,(await Get(admin,"/api/admin/payments/payos/requests?status=Paid&from=2020-01-01T00:00:00Z")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.GetAsync("/api/admin/payments/payos/checkouts?status=Unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.GetAsync("/api/admin/payments/payos/requests?from=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.GetAsync("/api/admin/payments/payos/requests?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.GetAsync("/api/admin/payments/payos/checkouts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/billing/quotations/{multi}/checkouts")).StatusCode);
        Assert.Equal(1,(await Get(admin,$"/api/billing/quotations/{multi}/checkouts")).GetProperty("total").GetInt32());
        // The request list reports the payment request time it sorts by, not the checkout time.
        await db.Sql($"ALTER TABLE payos_payment_requests DISABLE TRIGGER USER; UPDATE payos_payment_requests SET created_at='2021-03-04T05:06:07Z' WHERE quotation_id='{multi}'; ALTER TABLE payos_payment_requests ENABLE TRIGGER USER");
        Assert.Equal(new DateTime(2021,3,4,5,6,7,DateTimeKind.Utc),(await Get(admin,$"/api/admin/payments/payos/requests?quotationId={multi}")).GetProperty("items")[0].GetProperty("createdAt").GetDateTime().ToUniversalTime());
        // A revoked session family cannot read the ledger lists.
        await db.Sql($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id IN ('{BillingDatabase.Owner}','{BillingDatabase.Admin}')");
        Assert.Equal(HttpStatusCode.Unauthorized,(await owner.GetAsync($"/api/billing/quotations/{multi}/checkouts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await admin.GetAsync("/api/admin/payments/payos/checkouts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await admin.GetAsync("/api/admin/payments/payos/requests")).StatusCode);
    }
}
