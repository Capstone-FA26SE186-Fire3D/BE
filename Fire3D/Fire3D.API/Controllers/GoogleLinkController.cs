using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Authorize]
[Route("api/me/link-google")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GoogleLinkController(IGoogleLinkService linking) : ControllerBase
{
    /// <summary>Explicitly links a verified Google identity to the signed-in local account.</summary>
    /// <remarks>Requires a live BE Bearer session, currentPassword and a Google Firebase idToken.
    /// Email/role/tenant remain unchanged. A UID belonging to another account cannot be linked or replaced.
    /// First link revokes sessions/reset proofs and returns requiresLogin=true; sign in again.
    /// Repeating the same UID with a new valid session and current password does not create another audit.
    /// 401 invalid identity/session/password; 503 Google provider unavailable; 409 ownership/password reset race.</remarks>
    [HttpPost]
    [ProducesResponseType<GoogleLinkResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Link([FromBody] GoogleLinkRequest request, CancellationToken ct)
    {
        var result = await linking.LinkAsync(User.GetActorId(), User.GetSessionFamilyId(), request, ct);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            var problem = new ProblemDetails { Title = error.Message, Status = error.Status };
            problem.Extensions["code"] = error.Code;
            problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier;
            if (error.Errors is not null) problem.Extensions["errors"] = error.Errors;
            return StatusCode(error.Status, problem);
        }
        Response.Headers.ETag = ProfileEtag.Format(result.Value!.User.ProfileRevision);
        return Ok(result.Value);
    }
}
