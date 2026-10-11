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
/// <summary>BuildingId is null for an AI top-up line.</summary>
public sealed record ProvisioningLineResponse(Guid QuotationItemId,Guid? BuildingId,string Status,Guid? EntitlementId,string? ErrorCode);
/// <summary>
/// One checkout or payment request with its quotation, transaction and aggregated provisioning. Checkout, payment and
/// provisioning statuses stay separate: Paid does not mean provisioned. ProvisioningStatus: NotStarted, Pending, Partial,
/// Succeeded or NeedsReconcile. The checkout URL is read from the checkout detail route, never from lists.
/// </summary>
public sealed class PayosLedgerItem
{
    public Guid? CheckoutId { get; init; }
    public string? CheckoutStatus { get; init; }
    public string? CheckoutError { get; init; }
    public Guid QuotationId { get; init; }
    public string QuotationNumber { get; init; }="";
    public Guid OrganizationId { get; init; }
    public string Purpose { get; init; }="";
    public Guid? PaymentRequestId { get; init; }
    public string? PaymentStatus { get; init; }
    public Guid? TransactionId { get; init; }
    public string? TransactionStatus { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; }="";
    public string ProvisioningStatus { get; init; }="";
    public int ProvisioningLines { get; init; }
    public int ProvisioningSucceeded { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? PaidAt { get; init; }
}
public sealed record PayosLedgerQuery(Guid? OrganizationId=null,Guid? QuotationId=null,string? Status=null,string? From=null,string? To=null,int Page=1,int PageSize=20);
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
    Task<BillingPage<PayosLedgerItem>> AdminCheckouts(Guid actor,PayosLedgerQuery query,CancellationToken ct);
    Task<BillingPage<PayosLedgerItem>> AdminRequests(Guid actor,PayosLedgerQuery query,CancellationToken ct);
    Task<BillingPage<PayosLedgerItem>> QuotationCheckouts(Guid actor,Guid quotationId,int page,int pageSize,CancellationToken ct);
}
