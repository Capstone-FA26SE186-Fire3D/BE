using System.Text.Json;
namespace Fire3D.Application.Billing;
public sealed record PayosCreateInput(long OrderCode,long Amount,string Description,string ReturnUrl,string CancelUrl,DateTime ExpiresAt);
public sealed record PayosLink(long OrderCode,long Amount,string Currency,string PaymentLinkId,string Status,string? CheckoutUrl=null,string? QrCode=null,long AmountPaid=0);
public sealed record VerifiedPayosEvent(long OrderCode,long Amount,string Currency,string PaymentLinkId,string Reference,string TransactionDateTime,string? SignedDataHash=null);
public interface IPayosProvider
{
    Task<PayosLink> Create(PayosCreateInput input,CancellationToken ct);
    Task<PayosLink?> Get(long orderCode,CancellationToken ct);
    Task<PayosLink> Cancel(long orderCode,CancellationToken ct);
    Task<VerifiedPayosEvent> Verify(JsonElement body,CancellationToken ct);
}
public sealed record CreatePayosRequest(Guid QuotationId);
public sealed record PayosCheckoutResponse(Guid CheckoutId,Guid QuotationId,Guid? PaymentRequestId,long OrderCode,
    decimal Amount,string Currency,string CheckoutStatus,string? CheckoutUrl,string? QrCode,DateTime ExpiresAt,string? ErrorCode);
public sealed record CheckoutResult(PayosCheckoutResponse Value,int HttpStatus);
public sealed record ProvisioningLineResponse(Guid QuotationItemId,Guid BuildingId,string Status,Guid? EntitlementId,string? ErrorCode);
public sealed record PayosPaymentResponse(Guid Id,Guid QuotationId,long OrderCode,decimal Amount,string Currency,
    string PaymentStatus,DateTime? PaidAt,Guid? TransactionId,string? TransactionStatus,string ProvisioningStatus,IReadOnlyList<ProvisioningLineResponse> Items);
public sealed record EntitlementResponse(Guid Id,Guid BuildingId,string Status,bool IsEffective,DateTime StartsAt,DateTime EndsAt,Guid? PaymentTransactionId,int CommercialVersion=1,int? LearnerLimit=null);
public interface IPayosPayments
{
    Task<CheckoutResult> Create(Guid actor,Guid family,CreatePayosRequest request,string? key,CancellationToken ct);
    Task<PayosCheckoutResponse> Checkout(Guid actor,Guid id,CancellationToken ct);
    Task<PayosPaymentResponse> Payment(Guid actor,Guid id,CancellationToken ct);
    Task<CheckoutResult> Cancel(Guid actor,Guid family,Guid id,string? key,CancellationToken ct);
    Task Receive(JsonElement body,CancellationToken ct);
    Task Reconcile(Guid actor,Guid family,Guid checkoutId,CancellationToken ct);
    Task<BillingPage<EntitlementResponse>> Entitlements(Guid actor,Guid? organizationId,Guid? buildingId,int page,int pageSize,CancellationToken ct);
    Task Recover(CancellationToken ct);
}
