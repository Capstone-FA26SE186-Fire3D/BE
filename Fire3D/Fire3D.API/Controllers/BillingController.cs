using Fire3D.API.Authorization;
using Fire3D.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Authorize(Roles="OrganizationUser,PlatformAdmin")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
[ProducesResponseType(typeof(ProblemDetails),400)]
[ProducesResponseType(typeof(ProblemDetails),401)]
[ProducesResponseType(typeof(ProblemDetails),403)]
[ProducesResponseType(typeof(ProblemDetails),404)]
[ProducesResponseType(typeof(ProblemDetails),409)]
[ProducesResponseType(typeof(ProblemDetails),412)]
[ProducesResponseType(typeof(ProblemDetails),428)]
public sealed class BillingController(IBillingService billing) : ControllerBase
{
    private Guid Actor=>User.GetActorId();
    private void ETag(Guid id,long revision)=>Response.Headers.ETag=BillingETag.Format(id,revision);
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpPost("api/admin/billing/quota-policies")]
    [ProducesResponseType<QuotaPolicyResponse>(201)]
    public async Task<ActionResult<QuotaPolicyResponse>> CreateQuotaPolicy(QuotaPolicyWriteRequest request,CancellationToken ct)
    {var value=await billing.CreateQuotaPolicy(Actor,User.GetSessionFamilyId(),request,ct);return Created($"/api/admin/billing/quota-policies/{value.Id}",value);}
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/billing/quota-policies")]
    [ProducesResponseType<BillingPage<QuotaPolicyResponse>>(200)]
    public async Task<ActionResult<BillingPage<QuotaPolicyResponse>>> QuotaPolicies(int page=1,int pageSize=20,CancellationToken ct=default)=>Ok(await billing.ListQuotaPolicies(Actor,page,pageSize,ct));
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/billing/quota-policies/{id:guid}")]
    [ProducesResponseType<QuotaPolicyResponse>(200)]
    public async Task<ActionResult<QuotaPolicyResponse>> QuotaPolicy(Guid id,CancellationToken ct)=>Ok(await billing.GetQuotaPolicy(Actor,id,ct));
    /// <summary>Lists Building packages. OrganizationUser sees active packages; Admin also sees inactive packages.</summary>
    [HttpGet("api/billing/service-packages")]
    [ProducesResponseType<IReadOnlyList<PackageResponse>>(200)]
    public async Task<ActionResult<IReadOnlyList<PackageResponse>>> Packages(CancellationToken ct)=>Ok(await billing.ListPackages(Actor,ct));
    [HttpGet("api/billing/service-packages/{id:guid}")]
    [ProducesResponseType<PackageResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<PackageResponse>> Package(Guid id,CancellationToken ct)
    {
        var item=(await billing.ListPackages(Actor,ct)).SingleOrDefault(x=>x.Id==id);
        if(item is null)throw new BillingException(404,"BILLING_RESOURCE_NOT_FOUND","Package was not found.");
        ETag(id,item.Revision);return Ok(item);
    }
    /// <summary>Admin creates a VND package. unitPrice is the monthly price per Building; durationMonths is the package term.</summary>
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpPost("api/admin/service-packages")]
    [ProducesResponseType(typeof(PackageResponse),201)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<PackageResponse>> CreatePackage(PackageWriteRequest request,CancellationToken ct)
    {var response=await billing.SavePackage(Actor,User.GetSessionFamilyId(),null,request,null,ct);ETag(response.Id,response.Revision);return Created($"/api/billing/service-packages/{response.Id}",response);}
    /// <summary>Admin replaces all editable package fields, preserving identity/history. Supply the complete editable body and current If-Match.</summary>
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpPatch("api/admin/service-packages/{id:guid}")]
    [ProducesResponseType<PackageResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<PackageResponse>> UpdatePackage(Guid id,PackageWriteRequest request,[FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader]string? ifMatch,CancellationToken ct)
    {var response=await billing.SavePackage(Actor,User.GetSessionFamilyId(),id,request,ifMatch,ct);ETag(id,response.Revision);return Ok(response);}
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/discount-rules")]
    [ProducesResponseType<IReadOnlyList<DiscountResponse>>(200)]
    public async Task<ActionResult<IReadOnlyList<DiscountResponse>>> Discounts(CancellationToken ct)=>Ok(await billing.ListDiscounts(Actor,ct));
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/discount-rules/{id:guid}")]
    [ProducesResponseType<DiscountResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<DiscountResponse>> Discount(Guid id,CancellationToken ct)
    {var item=(await billing.ListDiscounts(Actor,ct)).SingleOrDefault(x=>x.Id==id);if(item is null)throw new BillingException(404,"BILLING_RESOURCE_NOT_FOUND","Discount was not found.");ETag(id,item.Revision);return Ok(item);}
    /// <summary>Admin creates a single eligible discount rule. Percent has at most two decimals; Fixed is whole VND.</summary>
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpPost("api/admin/discount-rules")]
    [ProducesResponseType(typeof(DiscountResponse),201)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<DiscountResponse>> CreateDiscount(DiscountWriteRequest request,CancellationToken ct)
    {var response=await billing.SaveDiscount(Actor,User.GetSessionFamilyId(),null,request,null,ct);ETag(response.Id,response.Revision);return Created($"/api/admin/discount-rules/{response.Id}",response);}
    /// <summary>Admin replaces the editable discount rule body with If-Match; issued quotation snapshots stay unchanged.</summary>
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpPatch("api/admin/discount-rules/{id:guid}")]
    [ProducesResponseType<DiscountResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<DiscountResponse>> UpdateDiscount(Guid id,DiscountWriteRequest request,[FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader]string? ifMatch,CancellationToken ct)
    {var response=await billing.SaveDiscount(Actor,User.GetSessionFamilyId(),id,request,ifMatch,ct);ETag(id,response.Revision);return Ok(response);}
    /// <summary>OrganizationUser creates a Draft for their own Buildings. Idempotency-Key is required; totals are server-calculated.</summary>
    [HttpPost("api/billing/quotations")]
    [ProducesResponseType(typeof(QuotationResponse),201)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<QuotationResponse>> CreateQuote(QuotationWriteRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)
    {var response=await billing.CreateQuotation(Actor,User.GetSessionFamilyId(),request,key,ct);ETag(response.Id,response.Revision);return Created($"/api/billing/quotations/{response.Id}",response);}
    [HttpGet("api/billing/quotations")]
    [ProducesResponseType<BillingPage<QuotationResponse>>(200)]
    public async Task<ActionResult<BillingPage<QuotationResponse>>> Quotes([FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)=>Ok(await billing.ListQuotations(Actor,page,pageSize,ct));
    [HttpGet("api/billing/quotations/{id:guid}")]
    [ProducesResponseType<QuotationResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<QuotationResponse>> Quote(Guid id,CancellationToken ct)
    {var response=await billing.GetQuotation(Actor,id,ct);ETag(id,response.Revision);return Ok(response);}
    /// <summary>Replaces Draft Building selections using If-Match. Issued/Accepted lines are immutable.</summary>
    [HttpPatch("api/billing/quotations/{id:guid}")]
    [ProducesResponseType<QuotationResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<QuotationResponse>> UpdateDraft(Guid id,QuotationWriteRequest request,[FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader]string? ifMatch,CancellationToken ct)
    {var response=await billing.UpdateDraft(Actor,User.GetSessionFamilyId(),id,request,ifMatch,ct);ETag(id,response.Revision);return Ok(response);}
    /// <summary>Admin reprices and freezes the v7 snapshot: monthly price, 6/12 month term, learner limit, quota/policy and fixed UTC service periods. Provide items with quotationItemId/startsAt, taxAmount, terms, validUntil and If-Match. Renewal derives an existing committed end; payment deadline cannot exceed the earliest start.</summary>
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpPost("api/admin/quotations/{id:guid}/issue")]
    [ProducesResponseType<QuotationResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<QuotationResponse>> Issue(Guid id,IssueQuotationRequest request,[FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader]string? ifMatch,CancellationToken ct)
    {var response=await billing.IssueQuotation(Actor,User.GetSessionFamilyId(),id,request,ifMatch,ct);ETag(id,response.Revision);return Ok(response);}
    /// <summary>OrganizationUser accepts an unexpired Issued quotation in their tenant with If-Match. Acceptance does not charge or grant service.</summary>
    [HttpPost("api/billing/quotations/{id:guid}/accept")]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<QuotationResponse>> Accept(Guid id,[FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader]string? ifMatch,CancellationToken ct)
    {var response=await billing.AcceptQuotation(Actor,User.GetSessionFamilyId(),id,ifMatch,ct);ETag(id,response.Revision);return Ok(response);}
    /// <summary>OrganizationUser submits a contact request with Idempotency-Key. This does not create a payment or entitlement.</summary>
    [HttpPost("api/billing/enterprise-quote-requests")]
    [ProducesResponseType(typeof(EnterpriseQuoteResponse),201)]
    public async Task<ActionResult<EnterpriseQuoteResponse>> Enterprise(EnterpriseQuoteRequestBody request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)
    {var response=await billing.CreateEnterpriseRequest(Actor,User.GetSessionFamilyId(),request,key,ct);return Created("/api/billing/enterprise-quote-requests",response);}
    [HttpGet("api/billing/enterprise-quote-requests")]
    public async Task<ActionResult<BillingPage<EnterpriseQuoteResponse>>> EnterpriseRequests([FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)=>Ok(await billing.ListEnterpriseRequests(Actor,page,pageSize,ct));
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/enterprise-quote-requests")]
    public async Task<ActionResult<BillingPage<EnterpriseQuoteResponse>>> AdminEnterpriseRequests([FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)=>Ok(await billing.ListEnterpriseRequests(Actor,page,pageSize,ct));
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/enterprise-quote-requests/{id:guid}")]
    [ProducesResponseType<EnterpriseQuoteResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<EnterpriseQuoteResponse>> AdminEnterpriseRequest(Guid id,CancellationToken ct)
    {var response=await billing.GetEnterpriseRequest(Actor,id,ct);ETag(id,response.Revision);return Ok(response);}
    /// <summary>Admin changes status to Contacted, Rejected or Cancelled with If-Match. No charge or service grant.</summary>
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpPatch("api/admin/enterprise-quote-requests/{id:guid}")]
    [ProducesResponseType<EnterpriseQuoteResponse>(200)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<EnterpriseQuoteResponse>> AdminEnterpriseStatus(Guid id,EnterpriseStatusRequest request,[FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader]string? ifMatch,CancellationToken ct)
    {var response=await billing.UpdateEnterpriseStatus(Actor,User.GetSessionFamilyId(),id,request,ifMatch,ct);ETag(id,response.Revision);return Ok(response);}
    /// <summary>Admin drafts a Building quotation for the requesting OrganizationUser and marks the request Quoted. Idempotency-Key required; no service is granted.</summary>
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpPost("api/admin/enterprise-quote-requests/{id:guid}/quotations")]
    [ProducesResponseType<QuotationResponse>(201)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<QuotationResponse>> AdminEnterpriseQuotation(Guid id,EnterpriseQuotationRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)
    {var response=await billing.CreateEnterpriseQuotation(Actor,User.GetSessionFamilyId(),id,request,key,ct);ETag(response.Id,response.Revision);return Created($"/api/billing/quotations/{response.Id}",response);}
    /// <summary>Current and upcoming paid service of a Building: effective learner limit (with upgrades), seats used/remaining, period and upgrades.</summary>
    [HttpGet("api/buildings/{id:guid}/service-entitlement")]
    [ProducesResponseType<BuildingServiceEntitlementResponse>(200)]
    public async Task<ActionResult<BuildingServiceEntitlementResponse>> BuildingEntitlement(Guid id,CancellationToken ct)=>Ok(await billing.GetBuildingEntitlement(Actor,id,ct));
    /// <summary>Organization prepaid AI quota per unit: granted, reserved, consumed, expired, available and scheduled at asOf.</summary>
    [HttpGet("api/organizations/me/ai-quota")]
    [ProducesResponseType<AiQuotaBalanceResponse>(200)]
    public async Task<ActionResult<AiQuotaBalanceResponse>> AiQuota(CancellationToken ct)=>Ok(await billing.GetAiQuota(Actor,null,ct));
    [HttpGet("api/organizations/me/ai-quota/grants")]
    [ProducesResponseType<BillingPage<AiQuotaGrantView>>(200)]
    public async Task<ActionResult<BillingPage<AiQuotaGrantView>>> AiQuotaGrants([FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)=>Ok(await billing.ListAiQuotaGrants(Actor,null,page,pageSize,ct));
    [HttpGet("api/organizations/me/ai-usage")]
    [ProducesResponseType<BillingPage<AiUsageView>>(200)]
    public async Task<ActionResult<BillingPage<AiUsageView>>> AiUsage([FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)=>Ok(await billing.ListAiUsage(Actor,null,page,pageSize,ct));
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/organizations/{organizationId:guid}/ai-quota")]
    [ProducesResponseType<AiQuotaBalanceResponse>(200)]
    public async Task<ActionResult<AiQuotaBalanceResponse>> AdminAiQuota(Guid organizationId,CancellationToken ct)=>Ok(await billing.GetAiQuota(Actor,organizationId,ct));
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/organizations/{organizationId:guid}/ai-quota/grants")]
    [ProducesResponseType<BillingPage<AiQuotaGrantView>>(200)]
    public async Task<ActionResult<BillingPage<AiQuotaGrantView>>> AdminAiQuotaGrants(Guid organizationId,[FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)=>Ok(await billing.ListAiQuotaGrants(Actor,organizationId,page,pageSize,ct));
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("api/admin/organizations/{organizationId:guid}/ai-usage")]
    [ProducesResponseType<BillingPage<AiUsageView>>(200)]
    public async Task<ActionResult<BillingPage<AiUsageView>>> AdminAiUsage(Guid organizationId,[FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)=>Ok(await billing.ListAiUsage(Actor,organizationId,page,pageSize,ct));
}
