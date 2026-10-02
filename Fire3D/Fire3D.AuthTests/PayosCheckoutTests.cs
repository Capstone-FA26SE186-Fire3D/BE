using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class FakePayos : IPayosProvider
{
    public ConcurrentDictionary<long,PayosLink> Links {get;}=new();
    public int Creates;
    public int Gets;
    public int Cancels;
    public bool LoseCreateResponse;
    public bool LoseCancelResponse;
    public Func<Task>? BeforeCreate;
    public async Task<PayosLink> Create(PayosCreateInput input,CancellationToken ct)
    {
        Interlocked.Increment(ref Creates);if(BeforeCreate is not null)await BeforeCreate();
        var link=new PayosLink(input.OrderCode,input.Amount,"VND","link"+input.OrderCode,"Pending","https://pay.payos.vn/web/link"+input.OrderCode,"test-qr");
        Links[input.OrderCode]=link;if(LoseCreateResponse){LoseCreateResponse=false;throw new TimeoutException();}return link;
    }
    public Task<PayosLink?> Get(long order,CancellationToken ct){Interlocked.Increment(ref Gets);return Task.FromResult(Links.TryGetValue(order,out var link)?link:null);}
    public Task<PayosLink> Cancel(long order,CancellationToken ct)
    {
        Interlocked.Increment(ref Cancels);var link=Links[order] with {Status="Cancelled"};Links[order]=link;
        if(LoseCancelResponse){LoseCancelResponse=false;throw new TimeoutException();}return Task.FromResult(link);
    }
    public Task<VerifiedPayosEvent> Verify(JsonElement body,CancellationToken ct)
    {
        if(body.TryGetProperty("invalidSignature",out _))throw new BillingException(400,"PAYOS_SIGNATURE_INVALID","Invalid test signature.");
        return Task.FromResult(body.Deserialize<VerifiedPayosEvent>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!);
    }
}
public sealed class PayosCheckoutTests
{
    [BillingPostgresFact]
    public async Task Cancellation_is_confirmed_by_provider_and_session_revocation_prevents_binding()
    {
        await using var db=await BillingDatabase.Create();var quote=await Accepted(db);var provider=new FakePayos();
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);
        var created=await Json(await owner.SendAsync(Create(quote)));var requestId=created.GetProperty("paymentRequestId").GetGuid();
        using var cancel=new HttpRequestMessage(HttpMethod.Post,$"/api/payments/payos/requests/{requestId}/cancel");cancel.Headers.Add("Idempotency-Key","cancel-1");
        var cancelled=await owner.SendAsync(cancel);Assert.Equal(HttpStatusCode.OK,cancelled.StatusCode);
        Assert.Equal("Cancelled",(await Json(cancelled)).GetProperty("checkoutStatus").GetString());
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        provider.BeforeCreate=()=>db.Sql($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{BillingDatabase.Owner}'");
        var second=await owner.SendAsync(Create(quote,"checkout-new"));Assert.Equal(HttpStatusCode.Accepted,second.StatusCode);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payos_payment_requests"));
        Assert.Equal("PAYOS_SESSION_REVOKED",(await Json(second)).GetProperty("errorCode").GetString());
    }
    internal static async Task<Guid> Accepted(BillingDatabase db,string action="New",Guid[]? buildings=null)
    {
        await using var context=db.Context();var billing=new Fire3D.Infrastructure.Billing.BillingService(context);
        var package=await billing.SavePackage(BillingDatabase.Admin,null,new("P"+Guid.NewGuid().ToString("N"),"Test",2000,1),null,default);
        var quote=await billing.CreateQuotation(BillingDatabase.Owner,new((buildings??[BillingDatabase.Building]).Select(id=>new QuotationItemRequest(id,package.Id,action)).ToArray()),Guid.NewGuid().ToString("N"),default);
        quote=await billing.IssueQuotation(BillingDatabase.Admin,quote.Id,new(0,"Test terms",DateTimeOffset.UtcNow.AddDays(1)),BillingETag.Format(quote.Id,quote.Revision),default);
        await billing.AcceptQuotation(BillingDatabase.Owner,quote.Id,BillingETag.Format(quote.Id,quote.Revision),default);return quote.Id;
    }
    internal static HttpRequestMessage Create(Guid id,string key="checkout-1")
    {var request=new HttpRequestMessage(HttpMethod.Post,"/api/payments/payos/create"){Content=JsonContent.Create(new {quotationId=id})};request.Headers.Add("Idempotency-Key",key);return request;}
    internal static async Task<JsonElement> Json(HttpResponseMessage response)
    {var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,text);return JsonDocument.Parse(text).RootElement.Clone();}
    [BillingPostgresFact]
    public async Task Create_replay_scope_and_no_provider_call_under_transaction()
    {
        await using var db=await BillingDatabase.Create();var quote=await Accepted(db);var provider=new FakePayos();
        provider.BeforeCreate=async()=>Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM billing_checkout_operations"));
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);using var other=factory.As(BillingDatabase.Other);
        var created=await owner.SendAsync(Create(quote));Assert.True(created.StatusCode==HttpStatusCode.Created,await created.Content.ReadAsStringAsync());var result=await Json(created);
        Assert.Equal("Ready",result.GetProperty("checkoutStatus").GetString());
        Assert.Equal(HttpStatusCode.OK,(await owner.SendAsync(Create(quote))).StatusCode);Assert.Equal(1,provider.Creates);
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync("/api/payments/payos/checkouts/"+result.GetProperty("checkoutId").GetGuid())).StatusCode);
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payos_payment_requests"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(HttpStatusCode.Conflict,(await owner.SendAsync(Create(Guid.NewGuid()))).StatusCode);
    }
    [BillingPostgresFact]
    public async Task Concurrent_keys_have_one_provider_call_and_lost_response_is_recovered()
    {
        await using var db=await BillingDatabase.Create();var quote=await Accepted(db);var provider=new FakePayos{LoseCreateResponse=true};
        using var factory=new BillingApiTests.Factory(db,provider);using var owner=factory.As(BillingDatabase.Owner);
        var attempts=await Task.WhenAll(owner.SendAsync(Create(quote)),owner.SendAsync(Create(quote,"checkout-2")));
        Assert.All(attempts,x=>Assert.Equal(HttpStatusCode.Accepted,x.StatusCode));Assert.Equal(1,provider.Creates);
        await db.Sql("UPDATE billing_checkout_operations SET next_attempt_at=now()");
        using var scope=factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPayosPayments>().Recover(default);
        var replay=await owner.SendAsync(Create(quote));var result=await Json(replay);Assert.Equal("Ready",result.GetProperty("checkoutStatus").GetString());
        Assert.Equal(1,provider.Creates);Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM payos_payment_requests"));
    }
}
