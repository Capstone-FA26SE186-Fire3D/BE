using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/auth/google/onboarding")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GoogleOnboardingController(IGoogleOnboardingService onboarding) : ControllerBase
{
    /// <summary>Completes verified Google onboarding as Trainee or OrganizationUser.</summary>
    /// <remarks>Use the 15-minute onboardingToken from login-firebase. Trainee requires username;
    /// OrganizationUser requires organizationName, organizationAddress and organizationPhoneNumber.
    /// Server creates a Google-only account; no password, role/tenant override or session is accepted.
    /// 201 first completion; 200 same normalized input replay within 24 hours; 409 different input.
    /// Exchange the Firebase token again to sign in after completion.</remarks>
    [HttpPost("complete")]
    [AllowAnonymous]
    [ProducesResponseType<AccountResponse>(201)]
    [ProducesResponseType<AccountResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
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
            return StatusCode(error.Status, problem);
        }
        return result.Value!.Replayed ? Ok(result.Value.User) : StatusCode(201, result.Value.User);
    }
}
