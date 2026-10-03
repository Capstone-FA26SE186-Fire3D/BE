using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class PayosContractTests
{
    [BillingPostgresFact]
    public async Task Navigation_is_public_and_cannot_mark_paid_and_OpenAPI_matches_authentication()
    {
        await using var db=await BillingDatabase.Create();using var factory=new BillingApiTests.Factory(db,new FakePayos());using var anonymous=factory.CreateClient();using var owner=factory.As(BillingDatabase.Owner);
        foreach(var path in new[]{"/billing/payment-return/?status=PAID&orderCode=123","/billing/payment-cancel/"})
        {
            var page=await anonymous.GetAsync(path);Assert.Equal(HttpStatusCode.OK,page.StatusCode);var html=await page.Content.ReadAsStringAsync();Assert.Contains("FET3D",html);Assert.Contains("/swagger/index.html",html);
        }
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM payment_transactions"));
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.PostAsJsonAsync("/api/payments/payos/create",new{quotationId=Guid.NewGuid()})).StatusCode);
        using var invalid=new HttpRequestMessage(HttpMethod.Post,"/api/payments/payos/create"){Content=JsonContent.Create(new {quotationId=Guid.Empty})};invalid.Headers.Add("Idempotency-Key","invalid");
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.SendAsync(invalid)).StatusCode);
        var api=JsonDocument.Parse(await anonymous.GetStringAsync("/openapi/v1.json")).RootElement;
        Assert.Equal("/",api.GetProperty("servers")[0].GetProperty("url").GetString());
        var webhook=api.GetProperty("paths").GetProperty("/api/payments/payos/webhook").GetProperty("post");
        Assert.True(!webhook.TryGetProperty("security",out var security)||security.GetArrayLength()==0);
        var create=api.GetProperty("paths").GetProperty("/api/payments/payos/create").GetProperty("post");
        Assert.NotEmpty(create.GetProperty("security").EnumerateArray());
        var key=create.GetProperty("parameters").EnumerateArray().Single(x=>x.GetProperty("name").GetString()=="Idempotency-Key");Assert.True(key.GetProperty("required").GetBoolean());
        var cancel=api.GetProperty("paths").GetProperty("/api/payments/payos/requests/{id}/cancel").GetProperty("post");
        Assert.True(cancel.GetProperty("parameters").EnumerateArray().Single(x=>x.GetProperty("name").GetString()=="Idempotency-Key").GetProperty("required").GetBoolean());
    }
    [BillingPostgresFact]
    public async Task Disabled_checkout_returns_explicit_503_and_missing_key_returns_400()
    {
        await using var db=await BillingDatabase.Create();using var disabled=new BillingApiTests.Factory(db);using var owner=disabled.As(BillingDatabase.Owner);
        var result=await owner.SendAsync(PayosCheckoutTests.Create(Guid.NewGuid()));Assert.Equal(HttpStatusCode.ServiceUnavailable,result.StatusCode);
        Assert.Equal("PAYOS_DISABLED",JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        using var enabled=new BillingApiTests.Factory(db,new FakePayos());using var enabledOwner=enabled.As(BillingDatabase.Owner);
        Assert.Equal(HttpStatusCode.BadRequest,(await enabledOwner.PostAsJsonAsync("/api/payments/payos/create",new{quotationId=Guid.NewGuid()})).StatusCode);
    }
}
