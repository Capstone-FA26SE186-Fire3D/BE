using System.Net;
using System.Text.Json;
using Fire3D.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Fire3D.AuthTests;

/// <summary>Provider stand-in whose create/get answers are scripted per test; unscripted calls behave like <see cref="FakePayos"/>.</summary>
internal sealed class ScriptedPayos : IPayosProvider
{
    public FakePayos Inner {get;}=new();
    public Func<PayosCreateInput,PayosLink>? OnCreate;
    public Task<PayosLink> Create(PayosCreateInput input,CancellationToken ct)=>OnCreate is null?Inner.Create(input,ct):Task.FromResult(OnCreate(input));
    public Task<PayosLink?> Get(long order,CancellationToken ct)=>Inner.Get(order,ct);
    public Task<PayosLink> Cancel(long order,CancellationToken ct)=>Inner.Cancel(order,ct);
    public Task<VerifiedPayosEvent> Verify(JsonElement body,CancellationToken ct)=>Inner.Verify(body,ct);
}

public sealed class PayosCheckoutRecoveryTests
{
    private static async Task<string?> Code(HttpResponseMessage response)
    { using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.TryGetProperty("code",out var code)?code.GetString():null; }
    private static async Task<Guid[]> Buildings(BillingDatabase db,int count)
    {
        var ids=Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToArray();
        foreach(var id in ids)await db.Sql($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{id}','{BillingDatabase.Org}','Extra',true,'{BillingDatabase.Owner}',now(),now()); INSERT INTO building_locations(building_id,address,created_at,updated_at) VALUES('{id}','Street',now(),now())");
        return ids;
    }
    // Fixture-only edits of immutable rows (legacy data): triggers are bypassed, constraints still apply.
    private static Task Replica(BillingDatabase db,string sql)=>db.Sql("SET session_replication_role=replica;"+sql+";SET session_replication_role=origin;");

    [BillingPostgresFact]
    public async Task Checkout_preconditions_reject_wrong_role_inactive_scope_legacy_and_paid_quotations()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);
        using var factory=new BillingApiTests.Factory(db,new FakePayos());using var owner=factory.As(BillingDatabase.Owner);
        using(var scope=factory.Services.CreateScope())
        {
            var error=await Assert.ThrowsAsync<BillingException>(()=>scope.ServiceProvider.GetRequiredService<IPayosPayments>().Create(BillingDatabase.Admin,BillingDatabase.Admin,new(quote),"admin",default));
            Assert.Equal((403,"BILLING_ROLE_FORBIDDEN"),(error.Status,error.Code));
        }
        await db.Sql($"UPDATE organizations SET is_active=false WHERE id='{BillingDatabase.Org}'");
        var inactive=await owner.SendAsync(PayosCheckoutTests.Create(quote,"inactive"));
        Assert.Equal(HttpStatusCode.Forbidden,inactive.StatusCode);Assert.Equal("ORGANIZATION_UNAVAILABLE",await Code(inactive));
        await db.Sql($"UPDATE organizations SET is_active=true WHERE id='{BillingDatabase.Org}'");
        await db.Sql($"UPDATE buildings SET is_active=false WHERE id='{BillingDatabase.Building}'");
        var building=await owner.SendAsync(PayosCheckoutTests.Create(quote,"building"));
        Assert.Equal(HttpStatusCode.Conflict,building.StatusCode);Assert.Equal("BUILDING_UNAVAILABLE",await Code(building));
        await db.Sql($"UPDATE buildings SET is_active=true WHERE id='{BillingDatabase.Building}'");
        await Replica(db,$"UPDATE quotations SET commercial_version=1 WHERE id='{quote}'");
        var legacy=await owner.SendAsync(PayosCheckoutTests.Create(quote,"legacy"));
        Assert.Equal(HttpStatusCode.Conflict,legacy.StatusCode);Assert.Equal("QUOTATION_V7_REQUIRED",await Code(legacy));
        await Replica(db,$"UPDATE quotations SET commercial_version=7,billing_purpose='AIQuotaTopUp' WHERE id='{quote}'");
        // A top-up quotation never carries Building lines.
        var mixed=await owner.SendAsync(PayosCheckoutTests.Create(quote,"mixed"));
        Assert.Equal(HttpStatusCode.Conflict,mixed.StatusCode);Assert.Equal("QUOTATION_V7_REQUIRED",await Code(mixed));
        await Replica(db,$"UPDATE quotations SET billing_purpose='BuildingService' WHERE id='{quote}'");
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM billing_checkout_operations"));

        var checkout=await PayosProvisioningTests.Paid(factory,quote,"paid");await PayosWebhookTests.Recover(factory,db);
        var read=await PayosCheckoutTests.Json(await owner.GetAsync($"/api/payments/payos/checkouts/{checkout.GetProperty("checkoutId").GetGuid()}"));
        Assert.Equal(quote,read.GetProperty("quotationId").GetGuid());
        var again=await owner.SendAsync(PayosCheckoutTests.Create(quote,"again"));
        Assert.Equal(HttpStatusCode.Conflict,again.StatusCode);Assert.Equal("QUOTATION_ALREADY_PAID",await Code(again));
        var entitlement=Assert.Single((await PayosCheckoutTests.Json(await owner.GetAsync($"/api/billing/entitlements?buildingId={BillingDatabase.Building}"))).GetProperty("items").EnumerateArray());
        Assert.Equal(BillingDatabase.Building,entitlement.GetProperty("buildingId").GetGuid());
        Assert.Equal(7,entitlement.GetProperty("commercialVersion").GetInt32());Assert.Equal(20,entitlement.GetProperty("learnerLimit").GetInt32());
        // The paid period starts in two days, so it is not effective yet.
        Assert.False(entitlement.GetProperty("isEffective").GetBoolean());
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_checkout_operations"));
    }

    [BillingPostgresFact]
    public async Task Provider_failures_park_checkout_with_classified_codes_and_mismatch_requests_cancel()
    {
        await using var db=await BillingDatabase.Create();var buildings=await Buildings(db,5);
        var provider=new ScriptedPayos();using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);
        async Task<(string? Code,bool Cancel,string Status)> Attempt(Guid building,Func<PayosCreateInput,PayosLink> create)
        {
            var quote=await PayosCheckoutTests.Accepted(db,buildings:[building]);provider.OnCreate=create;
            var response=await owner.SendAsync(PayosCheckoutTests.Create(quote,"key-"+building));
            Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
            var body=await PayosCheckoutTests.Json(response);
            Assert.Equal(JsonValueKind.Null,body.GetProperty("paymentRequestId").ValueKind);
            var cancel=(bool)(await db.Scalar($"SELECT cancel_requested FROM billing_checkout_operations WHERE quotation_id='{quote}'"))!;
            return (body.GetProperty("errorCode").GetString(),cancel,body.GetProperty("checkoutStatus").GetString()!);
        }
        Assert.Equal(("PAYOS_PROVIDER_MISMATCH",true,"NeedsReconcile"),await Attempt(buildings[0],input=>new(input.OrderCode,input.Amount+1,"VND","link"+input.OrderCode,"Pending")));
        Assert.Equal(("PAYOS_PROVIDER_HTTP_502",false,"NeedsReconcile"),await Attempt(buildings[1],_=>throw new PayOS.Exceptions.InternalServerErrorException(502,"Bad gateway","provider-error",null)));
        Assert.Equal(("PAYOS_RESPONSE_SIGNATURE_INVALID",false,"NeedsReconcile"),await Attempt(buildings[2],_=>throw new PayOS.Exceptions.InvalidSignatureException("tampered")));
        Assert.Equal(("PAYOS_PROVIDER_UNAVAILABLE",false,"NeedsReconcile"),await Attempt(buildings[3],_=>throw new InvalidOperationException("socket closed")));
        // Money on an unbound link is only trusted from the verified webhook.
        Assert.Equal(("PAYOS_AWAITING_VERIFIED_WEBHOOK",false,"NeedsReconcile"),await Attempt(buildings[4],input=>new(input.OrderCode,input.Amount,"VND","link"+input.OrderCode,"Pending",AmountPaid:1)));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payos_payment_requests"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_checkout_operations WHERE provider_result IS NOT NULL AND last_error='PAYOS_AWAITING_VERIFIED_WEBHOOK'"));
    }

    [BillingPostgresFact]
    public async Task Caller_cancellation_during_the_provider_call_propagates_and_leaves_the_reservation_for_recovery()
    {
        await using var db=await BillingDatabase.Create();var quote=await PayosCheckoutTests.Accepted(db);
        var provider=new ScriptedPayos();using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);
        using var aborted=new CancellationTokenSource();
        provider.OnCreate=_=>{aborted.Cancel();throw new OperationCanceledException(aborted.Token);};
        using(var scope=factory.Services.CreateScope())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>scope.ServiceProvider.GetRequiredService<IPayosPayments>().Create(BillingDatabase.Owner,BillingDatabase.Owner,new(quote),"aborted",aborted.Token));
        // Nothing is classified as a provider failure: the leased reservation simply waits for its lease to lapse.
        Assert.Equal("Creating/",(string?)await db.Scalar($"SELECT status||'/'||COALESCE(last_error,'') FROM billing_checkout_operations WHERE quotation_id='{quote}'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_service_reservations WHERE status='Reserved'"));
        provider.OnCreate=null;
        await db.Sql("UPDATE billing_checkout_operations SET lease_until=now()-interval '1 second',next_attempt_at=now()-interval '5 seconds'");
        using(var scope=factory.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<IPayosPayments>().Recover(default);
        var ready=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(quote,"aborted")));
        Assert.Equal("Ready",ready.GetProperty("checkoutStatus").GetString());Assert.Equal(1,provider.Inner.Creates);
    }

    [BillingPostgresFact]
    public async Task Recovery_recreates_a_link_the_provider_never_stored_unless_cancel_was_requested()
    {
        await using var db=await BillingDatabase.Create();var buildings=await Buildings(db,1);
        var provider=new ScriptedPayos();using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);
        var lost=await PayosCheckoutTests.Accepted(db);var cancelled=await PayosCheckoutTests.Accepted(db,buildings:[buildings[0]]);
        foreach(var (quote,key) in new[]{(lost,"lost"),(cancelled,"cancelled")})
        {
            provider.Inner.LoseCreateResponse=true;
            var response=await owner.SendAsync(PayosCheckoutTests.Create(quote,key));
            Assert.Equal("PAYOS_PROVIDER_TIMEOUT",(await PayosCheckoutTests.Json(response)).GetProperty("errorCode").GetString());
        }
        // The provider never kept either link: a later lookup finds nothing.
        provider.Inner.Links.Clear();
        await db.Sql($"UPDATE billing_checkout_operations SET cancel_requested=true WHERE quotation_id='{cancelled}'");
        await db.Sql("UPDATE billing_checkout_operations SET next_attempt_at=now()-interval '5 seconds'");
        using(var scope=factory.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<IPayosPayments>().Recover(default);
        Assert.Equal(3,provider.Inner.Creates);Assert.Equal(2,provider.Inner.Gets);
        var recreated=await PayosCheckoutTests.Json(await owner.SendAsync(PayosCheckoutTests.Create(lost,"lost")));
        Assert.Equal("Ready",recreated.GetProperty("checkoutStatus").GetString());Assert.NotEqual(JsonValueKind.Null,recreated.GetProperty("paymentRequestId").ValueKind);
        Assert.Equal("NeedsReconcile/PAYOS_LINK_NOT_FOUND",(string?)await db.Scalar($"SELECT status||'/'||last_error FROM billing_checkout_operations WHERE quotation_id='{cancelled}'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payos_payment_requests"));
    }
}
