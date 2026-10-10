using System.Text.Json.Serialization;

namespace Fire3D.Application.Billing;

public sealed record PackageWriteRequest(string Code,string Name,decimal UnitPrice,int DurationMonths,bool IsActive=true,string? Description=null,int? LearnerLimit=null,int? AiQuotaUnits=null,Guid? AiPolicyVersionId=null);
public sealed record PackageResponse(Guid Id,string Code,string Name,decimal UnitPrice,string Currency,int DurationMonths,bool IsActive,string? Description,long Revision,int CommercialVersion=1,int? LearnerLimit=null,int? AiQuotaUnits=null,Guid? AiPolicyVersionId=null,string PricingBasis="Monthly",bool IsPurchasable=false);
public sealed record DiscountWriteRequest(string Code,string DiscountKind,decimal DiscountValue,int MinimumBuildings,
    [property:JsonConverter(typeof(BillingTimestampConverter))] DateTimeOffset ValidFrom,
    [property:JsonConverter(typeof(BillingTimestampConverter))] DateTimeOffset? ValidUntil=null,
    Guid? ServicePackageId=null,int? MinimumDurationMonths=null,bool IsActive=true);
public sealed record DiscountResponse(Guid Id,string Code,string DiscountKind,decimal DiscountValue,int MinimumBuildings,
    DateTime ValidFrom,DateTime? ValidUntil,Guid? ServicePackageId,int? MinimumDurationMonths,bool IsActive,long Revision);
public sealed record QuotationItemRequest(Guid BuildingId,Guid ServicePackageId,string PurchaseAction);
public sealed record QuotationWriteRequest(IReadOnlyList<QuotationItemRequest> Items);
public sealed record IssueQuotationRequest(decimal? TaxAmount,string? Terms,
    [property:JsonConverter(typeof(BillingTimestampConverter))] DateTimeOffset? ValidUntil,
    IReadOnlyList<QuotationPeriodRequest>? Items=null);
public sealed record QuotationPeriodRequest(Guid QuotationItemId,
    [property:JsonConverter(typeof(BillingTimestampConverter))] DateTimeOffset? StartsAt=null);
public sealed record QuotationLineResponse(Guid Id,Guid BuildingId,Guid ServicePackageId,string PurchaseAction,int DurationMonths,
    string BuildingName,string BuildingAddress,string PackageName,decimal UnitPrice,decimal SubtotalAmount,decimal DiscountAmount,decimal TotalAmount,
    int CommercialVersion=1,long? PackageRevision=null,int? LearnerLimit=null,int? AiQuotaUnits=null,Guid? AiPolicyVersionId=null,string? AiQuotaUnit=null,DateTime? StartsAt=null,DateTime? EndsAt=null);
public sealed record QuotationResponse(Guid Id,Guid OrganizationId,string QuotationNumber,string Status,long Revision,string Currency,
    decimal SubtotalAmount,decimal DiscountAmount,decimal TaxAmount,decimal TotalAmount,Guid? DiscountRuleId,
    string? Terms,DateTime ValidUntil,DateTime? AcceptedAt,IReadOnlyList<QuotationLineResponse> Items,int CommercialVersion=1);
public sealed record BillingPage<T>(IReadOnlyList<T> Items,int Total,int Page,int PageSize);
public sealed record EnterpriseQuoteRequestBody(int RequestedBuildingCount,int? RequestedDurationMonths,string ContactName,string ContactEmail,string? ContactPhone=null,string? Notes=null);
public sealed record EnterpriseQuoteResponse(Guid Id,Guid OrganizationId,int RequestedBuildingCount,int? RequestedDurationMonths,
    string ContactName,string ContactEmail,string? ContactPhone,string? Notes,string Status,DateTime CreatedAt);

public sealed record QuotaPolicyWriteRequest(string QuotaUnit,
    [property:JsonConverter(typeof(BillingTimestampConverter))] DateTimeOffset EffectiveFrom,
    [property:JsonConverter(typeof(BillingTimestampConverter))] DateTimeOffset? EffectiveUntil=null);
public sealed record QuotaPolicyResponse(Guid Id,string Audience,string PolicyKind,string QuotaUnit,DateTime EffectiveFrom,DateTime? EffectiveUntil,string Rollover);

public interface IBillingService
{
    Task<QuotaPolicyResponse> CreateQuotaPolicy(Guid actor,QuotaPolicyWriteRequest request,CancellationToken ct);
    Task<BillingPage<QuotaPolicyResponse>> ListQuotaPolicies(Guid actor,int page,int pageSize,CancellationToken ct);
    Task<QuotaPolicyResponse> GetQuotaPolicy(Guid actor,Guid id,CancellationToken ct);
    Task<IReadOnlyList<PackageResponse>> ListPackages(Guid actor,CancellationToken ct);
    Task<PackageResponse> SavePackage(Guid actor,Guid? id,PackageWriteRequest request,string? ifMatch,CancellationToken ct);
    Task<IReadOnlyList<DiscountResponse>> ListDiscounts(Guid actor,CancellationToken ct);
    Task<DiscountResponse> SaveDiscount(Guid actor,Guid? id,DiscountWriteRequest request,string? ifMatch,CancellationToken ct);
    Task<QuotationResponse> CreateQuotation(Guid actor,Guid family,QuotationWriteRequest request,string? key,CancellationToken ct);
    Task<QuotationResponse> UpdateDraft(Guid actor,Guid family,Guid id,QuotationWriteRequest request,string? ifMatch,CancellationToken ct);
    Task<QuotationResponse> GetQuotation(Guid actor,Guid id,CancellationToken ct);
    Task<BillingPage<QuotationResponse>> ListQuotations(Guid actor,int page,int pageSize,CancellationToken ct);
    Task<QuotationResponse> IssueQuotation(Guid actor,Guid family,Guid id,IssueQuotationRequest request,string? ifMatch,CancellationToken ct);
    Task<QuotationResponse> AcceptQuotation(Guid actor,Guid family,Guid id,string? ifMatch,CancellationToken ct);
    Task<EnterpriseQuoteResponse> CreateEnterpriseRequest(Guid actor,EnterpriseQuoteRequestBody request,string? key,CancellationToken ct);
    Task<BillingPage<EnterpriseQuoteResponse>> ListEnterpriseRequests(Guid actor,int page,int pageSize,CancellationToken ct);
}

public sealed class BillingException(int status,string code,string message,Dictionary<string,string[]>? errors=null) : Exception(message)
{
    public int Status { get; }=status;
    public string Code { get; }=code;
    public Dictionary<string,string[]>? Errors { get; }=errors;
}

public static class BillingETag
{
    public static string Format(Guid id,long revision)=>$"\"billing-{id:N}-{revision}\"";
    public static void Require(string? value,Guid id,long revision)
    {
        if(string.IsNullOrWhiteSpace(value)) throw new BillingException(428,"IF_MATCH_REQUIRED","Supply If-Match from the resource's ETag.");
        if(!System.Text.RegularExpressions.Regex.IsMatch(value,"^\"billing-[a-f0-9]{32}-[1-9][0-9]*\"$"))
            throw new BillingException(400,"INVALID_IF_MATCH","Use the exact strong ETag returned by this API.");
        if(value!=Format(id,revision)) throw new BillingException(412,"REVISION_MISMATCH","The resource changed. Read it again before updating.");
    }
}
