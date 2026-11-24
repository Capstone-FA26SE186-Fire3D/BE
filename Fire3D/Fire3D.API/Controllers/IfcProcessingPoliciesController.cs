using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// IFC processing limit (BE#219): how many new Geometry jobs an Organization may start per UTC day. PlatformAdmin sets a default and
/// per-Organization overrides as immutable policy versions; without any policy IfcProcessing:DailyLimitPerOrganization (20) applies.
/// Over the limit, POST /api/revisions/{id}/process answers 429 IFC_PROCESS_DAILY_LIMIT with Retry-After, limit and used.
/// </summary>
[ApiController]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IfcProcessingPoliciesController(IIfcProcessingPolicies policies) : ControllerBase
{
    /// <summary>Policy versions, newest first, and the default in effect. organizationId: one Organization; scope=Default: the default only.</summary>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("/api/admin/ifc-processing/policies")]
    [ProducesResponseType<IfcProcessingPolicyPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> List([FromQuery] Guid? organizationId, [FromQuery] string? scope, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Respond(await policies.ListAsync(User.GetActorId(), User.GetSessionFamilyId(), organizationId, scope, page, pageSize, ct));

    /// <summary>Adds a policy version: the default (no organizationId) or an Organization override (dailyLimit null removes it).</summary>
    /// <remarks>dailyLimit 0-100000 (0 stops new processing); effectiveFrom defaults to now. Earlier versions stay as history; nothing is edited in place.</remarks>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration)]
    [HttpPost("/api/admin/ifc-processing/policies")]
    [ProducesResponseType<IfcProcessingPolicyResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Create([FromBody] CreateIfcProcessingPolicyRequest request, CancellationToken ct)
    {
        var result = await policies.CreateAsync(User.GetActorId(), User.GetSessionFamilyId(), request, ct);
        return result.IsSuccess ? Created($"/api/admin/ifc-processing/policies?organizationId={result.Value!.OrganizationId}", result.Value) : Error(result.Error!);
    }

    /// <summary>One Organization's limit in effect and its use today (UTC).</summary>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration)]
    [HttpGet("/api/admin/organizations/{organizationId:guid}/ifc-processing")]
    [ProducesResponseType<IfcProcessingUsage>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> OrganizationUsage(Guid organizationId, CancellationToken ct)
        => Respond(await policies.UsageAsync(User.GetActorId(), User.GetSessionFamilyId(), organizationId, ct));

    /// <summary>The caller's Organization: daily limit, used today, remaining and the next reset (UTC midnight).</summary>
    [Authorize(Roles = "OrganizationUser")]
    [HttpGet("/api/organizations/me/ifc-processing")]
    [ProducesResponseType<IfcProcessingUsage>(200)]
    public async Task<IActionResult> MyUsage(CancellationToken ct)
        => Respond(await policies.UsageAsync(User.GetActorId(), User.GetSessionFamilyId(), null, ct));

    private IActionResult Respond<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value) : Error(result.Error!);

    private ObjectResult Error(AuthError error) => Problem(statusCode: error.Status, title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
}
