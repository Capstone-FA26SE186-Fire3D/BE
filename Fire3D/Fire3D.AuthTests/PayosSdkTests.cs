using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Models.Webhooks;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class PayosSdkTests
{
    private static PayosSdkProvider Provider()=>new(Options.Create(new PayosOptions {ClientId="client",ApiKey="api",ChecksumKey="checksum"}));
    [Fact]
    public async Task Official_signature_verification_rejects_tampering_and_unknown_fields()
    {
        var client=new PayOSClient("client","api","checksum");
        var webhook=new Webhook {Code="00",Success=true,Data=new WebhookData {OrderCode=123,Amount=2000,
            Reference="ref-1",PaymentLinkId="link-1",Code="00",Currency="VND",TransactionDateTime="2026-10-02 10:00:00"}};
        webhook.Signature=client.Crypto.CreateSignatureFromObject(webhook.Data,"checksum")!;
        var body=JsonSerializer.SerializeToElement(webhook);
        Assert.Equal(2000,(await Provider().Verify(body,default)).Amount);
        webhook.Data.Amount=3000;
        var error=await Assert.ThrowsAsync<BillingException>(()=>Provider().Verify(JsonSerializer.SerializeToElement(webhook),default));
        Assert.Equal("PAYOS_SIGNATURE_INVALID",error.Code);
        var unknown=JsonDocument.Parse(body.GetRawText().Replace("\"data\":{","\"data\":{\"extraSignedField\":\"unexpected\","));
        await Assert.ThrowsAsync<BillingException>(()=>Provider().Verify(unknown.RootElement,default));
    }
}
