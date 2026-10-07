using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/auth/google/onboarding")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[GoogleOnboardingModelState]
public sealed class GoogleOnboardingController(IGoogleOnboardingService onboarding) : ControllerBase
{
    /// <summary>Completes verified Google onboarding and issues an authenticated session.</summary>
    /// <remarks>Use the 15-minute onboardingToken from login-firebase. Trainee requires username;
    /// OrganizationUser requires organizationName, organizationAddress and organizationPhoneNumber.
    /// Server creates a Google-only account; no password, role/tenant override or session is accepted.
    /// 201 returns authentication after atomic account/session creation. Same input replay within 24 hours
    /// returns 409 ONBOARDING_ALREADY_COMPLETED; recover by exchanging a valid Firebase ID token.
    /// Different input returns 409 IDEMPOTENCY_KEY_CONFLICT. Organization phone must be unique across
    /// all organizations including inactive/deleted. Duplicate phone returns 409 ORGANIZATION_PHONE_EXISTS
    /// with errors.organizationPhoneNumber; rollback preserves the unexpired proof for a corrected request.
    /// Optional personal phone is independently unique across all users, including inactive/deleted accounts;
    /// duplicate returns 409 PHONE_NUMBER_EXISTS with errors.phoneNumber and does not consume proof.</remarks>
    [HttpPost("complete")]
    [AllowAnonymous]
    [ProducesResponseType<GoogleExchangeResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Complete([FromBody] GoogleOnboardingCompleteRequest request, CancellationToken ct)
    {
        var result = await onboarding.CompleteAsync(request, ct);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            var problem = new ProblemDetails { Title = error.Message, Status = error.Status };
            problem.Extensions["code"] = error.Code;
            problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier;
            if (error.Errors is not null) problem.Extensions["errors"] = error.Errors;
            if (error.RetryAfterSeconds is int retry) Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(error.Status, problem);
        }
        return StatusCode(201, new GoogleExchangeResponse("Authenticated", result.Value!.Authentication));
    }
}
