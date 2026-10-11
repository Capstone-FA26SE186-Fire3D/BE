using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingQuotationPricingTests
{
    private static async Task<JsonElement> Read(HttpClient client,Guid id)=>await PayosCheckoutTests.Json(await client.GetAsync($"/api/billing/quotations/{id}"));

    [BillingPostgresFact]
    public async Task Draft_tax_is_an_unset_estimate_issue_fixes_it_and_legacy_or_old_receipts_never_claim_an_approved_zero()
    {
        await using var db=await BillingDatabase.Create();var provider=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);
        Guid package;
        await using(var context=db.Context())
            package=(await new BillingService(context).SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("P"+Guid.NewGuid().ToString("N"),"Test",2000,6,true,null,20,0),null,default)).Id;
        var create=new HttpRequestMessage(HttpMethod.Post,"/api/billing/quotations"){Content=JsonContent.Create(new {items=new[]{new {buildingId=BillingDatabase.Building,servicePackageId=package,purchaseAction="New"}}})};
        create.Headers.Add("Idempotency-Key","pricing-create");
        var draft=await PayosCheckoutTests.Json(await owner.SendAsync(create));
        var id=draft.GetProperty("id").GetGuid();
        Assert.Equal("Estimated",draft.GetProperty("pricingStatus").GetString());
        Assert.Equal(JsonValueKind.Null,draft.GetProperty("taxAmount").ValueKind);
        Assert.True(draft.GetProperty("totalAmount").GetDecimal()>0);
        Assert.Equal(JsonValueKind.Null,(await Read(owner,id)).GetProperty("issuedAt").ValueKind);

        // A receipt written before pricingStatus existed is converted from its own Draft snapshot.
        await db.Sql("UPDATE billing_command_receipts SET response=response-'PricingStatus'-'IssuedAt'||jsonb_build_object('TaxAmount',0) WHERE idempotency_key='pricing-create'");

        // Issue with an explicit zero tax: Final, tax 0 is a fixed value.
        QuotationResponse issued;
        await using(var context=db.Context())
        {
            var billing=new BillingService(context);var current=await billing.GetQuotation(BillingDatabase.Owner,id,default);
            issued=await billing.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,id,new(0,"Terms",DateTimeOffset.UtcNow.AddDays(1),current.Items.Select(x=>new QuotationPeriodRequest(x.Id,new DateTimeOffset(DateTimeOffset.UtcNow.Date.AddDays(2),TimeSpan.Zero))).ToArray()),BillingETag.Format(id,current.Revision),default);
        }
        Assert.Equal("Final",issued.PricingStatus);Assert.Equal(0m,issued.TaxAmount);Assert.NotNull(issued.IssuedAt);
        var replay=new HttpRequestMessage(HttpMethod.Post,"/api/billing/quotations"){Content=JsonContent.Create(new {items=new[]{new {buildingId=BillingDatabase.Building,servicePackageId=package,purchaseAction="New"}}})};
        replay.Headers.Add("Idempotency-Key","pricing-create");
        var replayed=await PayosCheckoutTests.Json(await owner.SendAsync(replay));
        Assert.Equal("Draft",replayed.GetProperty("status").GetString());
        Assert.Equal("Estimated",replayed.GetProperty("pricingStatus").GetString());
        Assert.Equal(JsonValueKind.Null,replayed.GetProperty("taxAmount").ValueKind);

        // Cancelling after Issue keeps the fixed price; cancelling a Draft keeps it an estimate.
        var cancel=new HttpRequestMessage(HttpMethod.Post,$"/api/billing/quotations/{id}/cancel"){Content=JsonContent.Create(new {reason="No longer needed"})};
        cancel.Headers.TryAddWithoutValidation("If-Match",BillingETag.Format(id,issued.Revision));cancel.Headers.Add("Idempotency-Key","pricing-cancel");
        var cancelledIssued=await PayosCheckoutTests.Json(await owner.SendAsync(cancel));
        Assert.Equal("Final",cancelledIssued.GetProperty("pricingStatus").GetString());Assert.Equal(0m,cancelledIssued.GetProperty("taxAmount").GetDecimal());
        QuotationResponse other;
        await using(var context=db.Context())
            other=await new BillingService(context).CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new QuotationItemRequest(BillingDatabase.Building,package,"New")]),"pricing-other",default);
        var cancelDraft=new HttpRequestMessage(HttpMethod.Post,$"/api/billing/quotations/{other.Id}/cancel"){Content=JsonContent.Create(new {reason="Draft only"})};
        cancelDraft.Headers.TryAddWithoutValidation("If-Match",BillingETag.Format(other.Id,other.Revision));cancelDraft.Headers.Add("Idempotency-Key","pricing-cancel-draft");
        var cancelledDraft=await PayosCheckoutTests.Json(await owner.SendAsync(cancelDraft));
        Assert.Equal("Estimated",cancelledDraft.GetProperty("pricingStatus").GetString());Assert.Equal(JsonValueKind.Null,cancelledDraft.GetProperty("taxAmount").ValueKind);

        // A legacy non-Draft record without an issue timestamp is LegacyUnknown, never an approved zero tax.
        await db.Sql($"ALTER TABLE quotations DISABLE TRIGGER USER; UPDATE quotations SET commercial_version=1 WHERE id='{other.Id}'; ALTER TABLE quotations ENABLE TRIGGER USER;");
        var legacy=await Read(owner,other.Id);
        Assert.Equal("LegacyUnknown",legacy.GetProperty("pricingStatus").GetString());Assert.Equal(JsonValueKind.Null,legacy.GetProperty("taxAmount").ValueKind);
    }

    [BillingPostgresFact]
    public async Task Checkout_return_and_cancel_urls_come_from_configuration_not_from_the_client()
    {
        await using var db=await BillingDatabase.Create();var provider=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);
        var quote=await PayosCheckoutTests.Accepted(db);
        var request=new HttpRequestMessage(HttpMethod.Post,"/api/payments/payos/create"){Content=JsonContent.Create(new {quotationId=quote,returnUrl="https://attacker.example/return",cancelUrl="https://attacker.example/cancel"})};
        request.Headers.Add("Idempotency-Key","urls");
        Assert.True((await owner.SendAsync(request)).IsSuccessStatusCode);
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payos_payment_requests WHERE return_url LIKE '%attacker%' OR cancel_url LIKE '%attacker%'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payos_payment_requests WHERE return_url LIKE 'https://%' AND cancel_url LIKE 'https://%'"));
    }
}
