using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.Application.Billing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using PayOS;
using PayOS.Exceptions;
using PayOS.Models;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;
namespace Fire3D.Infrastructure.Billing;
public sealed class PayosSdkProvider : IPayosProvider
{
    private readonly PayOSClient client;
    public PayosSdkProvider(IOptions<PayosOptions> options)
    {
        var s=options.Value;
        client=new PayOSClient(new PayOSOptions {ClientId=s.ClientId,ApiKey=s.ApiKey,ChecksumKey=s.ChecksumKey,
            TimeoutMs=15000,MaxRetries=0,Logger=NullLogger.Instance,LogLevel=Microsoft.Extensions.Logging.LogLevel.None});
    }
    public async Task<PayosLink> Create(PayosCreateInput input,CancellationToken ct)
    {
        var response=await client.PaymentRequests.CreateAsync(new CreatePaymentLinkRequest
        {OrderCode=input.OrderCode,Amount=input.Amount,Description=input.Description,ReturnUrl=input.ReturnUrl,
            CancelUrl=input.CancelUrl,ExpiredAt=new DateTimeOffset(input.ExpiresAt).ToUnixTimeSeconds()},
            new RequestOptions<CreatePaymentLinkRequest> {CancellationToken=ct,MaxRetries=0});
        return new(response.OrderCode,response.Amount,response.Currency,response.PaymentLinkId,response.Status.ToString(),response.CheckoutUrl,response.QrCode);
    }
    public async Task<PayosLink?> Get(long orderCode,CancellationToken ct)
    {
        try { return View(await client.PaymentRequests.GetAsync(orderCode,new RequestOptions {CancellationToken=ct,MaxRetries=0})); }
        catch(ApiException ex) when(ex.StatusCode==404) { return null; }
    }
    public async Task<PayosLink> Cancel(long orderCode,CancellationToken ct)=>View(await client.PaymentRequests.CancelAsync(orderCode,
        "Cancelled by FET3D requester",new RequestOptions<CancelPaymentLinkRequest> {CancellationToken=ct,MaxRetries=0}));
    private static PayosLink View(PaymentLink link)=>new(link.OrderCode,link.Amount,"VND",link.Id,link.Status.ToString(),AmountPaid:link.AmountPaid);
    public async Task<VerifiedPayosEvent> Verify(JsonElement body,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            UniqueFields(body);
            var webhook=body.Deserialize<Webhook>(new JsonSerializerOptions {UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new JsonException();
            var data=await client.Webhooks.VerifyAsync(webhook);
            // Envelope success/code are unsigned. Only data.code authorizes a successful payment.
            if(data.Code!="00" || data.OrderCode<=0 || data.Amount<=0 || string.IsNullOrWhiteSpace(data.Reference)
                || data.Reference.Length>160 || string.IsNullOrWhiteSpace(data.PaymentLinkId) || data.PaymentLinkId.Length>100)
                throw new BillingException(400,"PAYOS_WEBHOOK_INVALID","The signed webhook does not describe a successful payment.");
            return new(data.OrderCode,data.Amount,data.Currency,data.PaymentLinkId,data.Reference,data.TransactionDateTime);
        }
        catch(Exception ex) when(ex is JsonException or WebhookException)
        { throw new BillingException(400,"PAYOS_SIGNATURE_INVALID","The PayOS webhook signature or signed data is invalid."); }
    }
    private static void UniqueFields(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in value.EnumerateObject()) {if(!names.Add(field.Name))throw new JsonException();UniqueFields(field.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())UniqueFields(item);
    }
}
