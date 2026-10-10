using Fire3D.API.Authorization;
using Fire3D.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;
[ApiController][Authorize][ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
[ProducesResponseType(typeof(ProblemDetails),400)][ProducesResponseType(typeof(ProblemDetails),401)]
[ProducesResponseType(typeof(ProblemDetails),403)][ProducesResponseType(typeof(ProblemDetails),404)]
[ProducesResponseType(typeof(ProblemDetails),409)][ProducesResponseType(typeof(ProblemDetails),503)]
public sealed class PayosController(IPayosPayments payments) : ControllerBase
{
    /// <summary>OrganizationUser creates/replays checkout for an Accepted quotation. Server determines amount and tenant.</summary>
    [Authorize(Roles="OrganizationUser")][HttpPost("api/payments/payos/create")]
    [ProducesResponseType(typeof(PayosCheckoutResponse),201)][ProducesResponseType(typeof(PayosCheckoutResponse),200)][ProducesResponseType(typeof(PayosCheckoutResponse),202)]
    public async Task<IActionResult> Create(CreatePayosRequest request,[FromHeader(Name="Idempotency-Key")]string? key,CancellationToken ct)
    {var result=await payments.Create(User.GetActorId(),User.GetSessionFamilyId(),request,key,ct);Response.Headers.Location=$"/api/payments/payos/checkouts/{result.Value.CheckoutId}";return StatusCode(result.HttpStatus,result.Value);}
    /// <summary>OrganizationUser reads own tenant checkout; PlatformAdmin may inspect all tenants.</summary>
    [Authorize(Roles="OrganizationUser,PlatformAdmin")][HttpGet("api/payments/payos/checkouts/{id:guid}")][ProducesResponseType(typeof(PayosCheckoutResponse),200)]
    public async Task<PayosCheckoutResponse> Checkout(Guid id,CancellationToken ct)=>await payments.Checkout(User.GetActorId(),id,ct);
    /// <summary>OrganizationUser requests provider cancellation. Requires Idempotency-Key; payment confirmation wins a concurrent cancel.</summary>
    /// <remarks>Same key/input returns current checkoutStatus without provider calls or mutation. Terminal replay returns200, including Completed after a late payment; otherwise202. Read checkoutStatus:200 alone does not mean Cancelled. A new cancellation of Paid returns409.</remarks>
    [Authorize(Roles="OrganizationUser")][HttpPost("api/payments/payos/requests/{id:guid}/cancel")]
    [ProducesResponseType(typeof(PayosCheckoutResponse),200)][ProducesResponseType(typeof(PayosCheckoutResponse),202)]
    public async Task<IActionResult> Cancel(Guid id,[FromHeader(Name="Idempotency-Key")]string? key,CancellationToken ct)
    {var result=await payments.Cancel(User.GetActorId(),User.GetSessionFamilyId(),id,key,ct);return StatusCode(result.HttpStatus,result.Value);}
    /// <summary>Anonymous JWT endpoint, authenticated by official PayOS signature. ACK only after durable inbox commit; navigation never marks Paid.</summary>
    [AllowAnonymous][HttpPost("api/payments/payos/webhook")][ProducesResponseType(200)]
    public async Task<IActionResult> Webhook(System.Text.Json.JsonElement body,CancellationToken ct){await payments.Receive(body,ct);return Ok();}
    /// <summary>Tenant-scoped payment truth and per-Building provisioning. Paid does not mean all lines succeeded.</summary>
    [Authorize(Roles="OrganizationUser,PlatformAdmin")][HttpGet("api/payments/payos/requests/{id:guid}")][ProducesResponseType(typeof(PayosPaymentResponse),200)]
    public async Task<PayosPaymentResponse> Payment(Guid id,CancellationToken ct)=>await payments.Payment(User.GetActorId(),id,ct);
    /// <summary>PlatformAdmin enqueues reconciliation; does not override amount, signature or entitlement provenance.</summary>
    [Authorize(Policy=AuthorizationPolicies.PlatformAdministration)][HttpPost("api/admin/payments/payos/checkouts/{id:guid}/reconcile")]
    [ProducesResponseType(202)]
    public async Task<IActionResult> Reconcile(Guid id,CancellationToken ct){await payments.Reconcile(User.GetActorId(),User.GetSessionFamilyId(),id,ct);return Accepted();}
    /// <summary>OrganizationUser sees own tenant. PlatformAdmin can filter organization/Building. IsEffective checks lifecycle and UTC period.</summary>
    [Authorize(Roles="OrganizationUser,PlatformAdmin")][HttpGet("api/billing/entitlements")]
    public async Task<BillingPage<EntitlementResponse>> Entitlements([FromQuery]Guid? organizationId,[FromQuery]Guid? buildingId,[FromQuery]int page=1,[FromQuery]int pageSize=20,CancellationToken ct=default)
        =>await payments.Entitlements(User.GetActorId(),organizationId,buildingId,page,pageSize,ct);
}
