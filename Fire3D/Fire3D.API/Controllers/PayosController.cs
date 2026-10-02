using Fire3D.API.Authorization;
using Fire3D.Application.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;
[ApiController][Authorize][ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class PayosController(IPayosPayments payments) : ControllerBase
{
    /// <summary>OrganizationUser creates/replays checkout for an Accepted quotation. Server determines amount and tenant.</summary>
    [HttpPost("api/payments/payos/create")]
    [ProducesResponseType(typeof(PayosCheckoutResponse),201)][ProducesResponseType(typeof(PayosCheckoutResponse),200)][ProducesResponseType(typeof(PayosCheckoutResponse),202)]
    public async Task<IActionResult> Create(CreatePayosRequest request,[FromHeader(Name="Idempotency-Key")]string? key,CancellationToken ct)
    {var result=await payments.Create(User.GetActorId(),User.GetSessionFamilyId(),request,key,ct);Response.Headers.Location=$"/api/payments/payos/checkouts/{result.Value.CheckoutId}";return StatusCode(result.HttpStatus,result.Value);}
    /// <summary>OrganizationUser reads own tenant checkout; PlatformAdmin may inspect all tenants.</summary>
    [HttpGet("api/payments/payos/checkouts/{id:guid}")]
    public async Task<PayosCheckoutResponse> Checkout(Guid id,CancellationToken ct)=>await payments.Checkout(User.GetActorId(),id,ct);
    /// <summary>OrganizationUser requests provider cancellation. Requires Idempotency-Key; payment confirmation wins a concurrent cancel.</summary>
    [HttpPost("api/payments/payos/requests/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id,[FromHeader(Name="Idempotency-Key")]string? key,CancellationToken ct)
    {var result=await payments.Cancel(User.GetActorId(),User.GetSessionFamilyId(),id,key,ct);return StatusCode(result.HttpStatus,result.Value);}
    /// <summary>Anonymous JWT endpoint, authenticated by official PayOS signature. ACK only after durable inbox commit; navigation never marks Paid.</summary>
    [AllowAnonymous][HttpPost("api/payments/payos/webhook")][ProducesResponseType(200)]
    public async Task<IActionResult> Webhook(System.Text.Json.JsonElement body,CancellationToken ct){await payments.Receive(body,ct);return Ok();}
}
