using System.Net;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Models.Webhooks;
using Xunit;
namespace Fire3D.AuthTests;

/// <summary>Signed PayOS responses parsed by the SDK adapter without network access, plus fail-closed configuration checks.</summary>
public sealed class PayosSdkResponseTests
{
    private const string Checksum="checksum";
    private sealed class SignedTransport(Func<HttpRequestMessage,object> data) : HttpMessageHandler,IHttpClientFactory
    {
        public readonly List<(HttpMethod Method,string Path)> Calls=[];
        public HttpClient CreateClient(string name)=>new(this,false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls.Add((request.Method,request.RequestUri!.AbsolutePath));
            using var client=new PayOSClient("client","api",Checksum);var payload=data(request);
            var body=JsonSerializer.Serialize(new{code="00",desc="success",data=payload,signature=client.Crypto.CreateSignatureFromObject(payload,Checksum)});
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    private static PayosSdkProvider Provider(IHttpClientFactory transport)=>new(Options.Create(new PayosOptions{ClientId="client",ApiKey="api",ChecksumKey=Checksum}),transport);

    [Fact]
    public async Task Create_and_cancel_map_verified_provider_responses()
    {
        using var transport=new SignedTransport(request=>request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath.EndsWith("/cancel")
            ? new Dictionary<string,object?>{["id"]="link-77",["orderCode"]=77L,["amount"]=2000L,["amountPaid"]=0L,["amountRemaining"]=2000L,["status"]="CANCELLED",["createdAt"]="2026-10-02T10:00:00+07:00",["transactions"]=Array.Empty<object>(),["cancellationReason"]="Cancelled by FET3D requester",["canceledAt"]="2026-10-02T10:05:00+07:00"}
            : new Dictionary<string,object?>{["bin"]="970422",["accountNumber"]="0001",["accountName"]="FET3D",["amount"]=2000L,["description"]="FET3D",["orderCode"]=77L,["currency"]="VND",["paymentLinkId"]="link-77",["status"]="PENDING",["expiredAt"]=1790000000L,["checkoutUrl"]="https://pay.payos.vn/web/link-77",["qrCode"]="qr-77"});
        using var provider=Provider(transport);
        var created=await provider.Create(new(77,2000,"FET3D","https://be.test/return","https://be.test/cancel",DateTime.UtcNow.AddMinutes(30)),default);
        Assert.Equal(new PayosLink(77,2000,"VND","link-77","Pending","https://pay.payos.vn/web/link-77","qr-77"),created);
        var cancelled=await provider.Cancel(77,default);
        Assert.Equal(new PayosLink(77,2000,"VND","link-77","Cancelled"),cancelled);
        Assert.Equal([(HttpMethod.Post,"/v2/payment-requests"),(HttpMethod.Post,"/v2/payment-requests/77/cancel")],transport.Calls);
    }

    [Fact]
    public async Task Signed_webhook_that_is_not_a_successful_payment_is_rejected()
    {
        using var client=new PayOSClient("client","api",Checksum);using var provider=new PayosSdkProvider(Options.Create(new PayosOptions{ClientId="client",ApiKey="api",ChecksumKey=Checksum}));
        var webhook=new Webhook{Code="00",Success=true,Data=new WebhookData{OrderCode=123,Amount=2000,Reference="ref-1",PaymentLinkId="link-1",Code="01",Currency="VND",TransactionDateTime="2026-10-02 10:00:00"}};
        webhook.Signature=client.Crypto.CreateSignatureFromObject(webhook.Data,Checksum)!;
        var error=await Assert.ThrowsAsync<BillingException>(()=>provider.Verify(JsonSerializer.SerializeToElement(webhook),default));
        Assert.Equal((400,"PAYOS_WEBHOOK_INVALID"),(error.Status,error.Code));
        // Nested arrays are checked for duplicate keys too.
        var duplicated=JsonDocument.Parse("""{"code":"00","desc":"success","success":true,"data":{"orderCode":1},"signature":"x","extra":[{"a":1,"a":2}]}""");
        Assert.Equal("PAYOS_SIGNATURE_INVALID",(await Assert.ThrowsAsync<BillingException>(()=>provider.Verify(duplicated.RootElement,default))).Code);
    }

    [Fact]
    public async Task Webhook_without_signature_configuration_is_refused_before_verification()
    {
        await using var db=new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql("Host=127.0.0.1;Database=unused").Options);
        var verified=false;
        var provider=ResetProxy.For<IPayosProvider>((_,_)=>{verified=true;throw new InvalidOperationException();});
        var payments=new PayosPayments(db,provider,new PayosExecutor(new ConfigurationBuilder().Build()),
            Options.Create(new PayosOptions{Enabled=true,ClientId="client",ApiKey="api",ChecksumKey=""}),TimeProvider.System,NullLogger<PayosPayments>.Instance);
        var error=await Assert.ThrowsAsync<BillingException>(()=>payments.Receive(JsonDocument.Parse("{}").RootElement,default));
        Assert.Equal((503,"PAYOS_NOT_CONFIGURED"),(error.Status,error.Code));Assert.False(verified);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=other")]
    [InlineData("Host=10.0.0.5;Port=5432;Database=app")]
    [InlineData("Host=127.0.0.1;Port=6543;Database=app")]
    public async Task Executor_refuses_a_connection_outside_the_application_database(string executor)
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {["ConnectionStrings:DefaultConnection"]="Host=127.0.0.1;Port=5432;Database=app",["ConnectionStrings:PayosWebhookExecutor"]=executor}).Build();
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>new PayosExecutor(configuration).Run(true,"SELECT 1",default));
        Assert.Equal("PayOS executor must target the application database.",error.Message);
        Assert.Equal("PayOS executor connection is not configured.",(await Assert.ThrowsAsync<InvalidOperationException>(()=>new PayosExecutor(configuration).Run(false,"SELECT 1",default))).Message);
    }
}
