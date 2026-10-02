using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Models.Webhooks;
using Xunit;
using System.Net;
using System.Text;
namespace Fire3D.AuthTests;
public sealed class PayosSdkTests
{
    private sealed class StubTransport(int status,string json) : HttpMessageHandler,IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name)=>new(this,false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(json,Encoding.UTF8,"application/json")});
        }
    }
    [Fact]
    public async Task Confirmed_http_404_is_absence_but_unknown_business_error_is_not()
    {
        using var missing=new StubTransport(404,"{\"code\":\"not-found\",\"desc\":\"test\"}");
        using var absent=new PayosSdkProvider(Options.Create(new PayosOptions{ClientId="client",ApiKey="api",ChecksumKey="checksum"}),missing);
        Assert.Null(await absent.Get(123,default));Assert.Equal(1,missing.Calls);
        using var unknown=new StubTransport(200,"{\"code\":\"unrecognized\",\"desc\":\"test\",\"data\":null}");
        using var uncertain=new PayosSdkProvider(Options.Create(new PayosOptions{ClientId="client",ApiKey="api",ChecksumKey="checksum"}),unknown);
        await Assert.ThrowsAsync<PayOS.Exceptions.ApiException>(()=>uncertain.Get(123,default));Assert.Equal(1,unknown.Calls);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Create_has_no_automatic_retry_on_provider_failure(int status)
    {
        using var transport=new StubTransport(status,"{\"code\":\"failure\",\"desc\":\"test\"}");
        using var provider=new PayosSdkProvider(Options.Create(new PayosOptions{ClientId="client",ApiKey="api",ChecksumKey="checksum"}),transport);
        var error=await Assert.ThrowsAnyAsync<PayOS.Exceptions.ApiException>(()=>provider.Create(new(123,2000,"FET3D","https://be.test/return","https://be.test/cancel",DateTime.UtcNow.AddMinutes(30)),default));
        Assert.Equal(status,error.StatusCode);
        Assert.Equal(1,transport.Calls);
    }

    private static PayosSdkProvider Provider()=>new(Options.Create(new PayosOptions {ClientId="client",ApiKey="api",ChecksumKey="checksum"}));
    [Fact]
    public async Task Official_signature_verification_rejects_tampering_and_unknown_fields()
    {
        using var client=new PayOSClient("client","api","checksum");using var provider=Provider();
        var webhook=new Webhook {Code="00",Success=true,Data=new WebhookData {OrderCode=123,Amount=2000,
            Reference="ref-1",PaymentLinkId="link-1",Code="00",Currency="VND",TransactionDateTime="2026-10-02 10:00:00"}};
        webhook.Signature=client.Crypto.CreateSignatureFromObject(webhook.Data,"checksum")!;
        var body=JsonSerializer.SerializeToElement(webhook);
        var first=await provider.Verify(body,default);Assert.Equal(2000,first.Amount);Assert.Matches("^[a-f0-9]{64}$",first.SignedDataHash!);
        webhook.Data.CounterAccountName="Changed signed bank detail";webhook.Signature=client.Crypto.CreateSignatureFromObject(webhook.Data,"checksum")!;
        Assert.NotEqual(first.SignedDataHash,(await provider.Verify(JsonSerializer.SerializeToElement(webhook),default)).SignedDataHash);
        webhook.Data.Amount=3000;
        var error=await Assert.ThrowsAsync<BillingException>(()=>provider.Verify(JsonSerializer.SerializeToElement(webhook),default));
        Assert.Equal("PAYOS_SIGNATURE_INVALID",error.Code);
        var unknown=JsonDocument.Parse(body.GetRawText().Replace("\"data\":{","\"data\":{\"extraSignedField\":\"unexpected\","));
        await Assert.ThrowsAsync<BillingException>(()=>provider.Verify(unknown.RootElement,default));
    }
}
