using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Authorize]
[Route("api/me/password")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GooglePasswordController(IGooglePasswordService passwords) : ControllerBase
{
    /// <summary>Sets the first local password of a Google-only account.</summary>
    /// <remarks>Requires a live BE Bearer session and the Firebase idToken of a Google sign-in made within the last
    /// 5 minutes for the linked Google account; sign in with Google again just before calling. newPassword uses the
    /// reset rules (6-128 characters, not only whitespace, not trimmed). Success revokes every session and reset proof
    /// and returns 204; sign in again with email/password or Google. GET /api/auth/me reports hasPassword/googleLinked.
    /// 400 VALIDATION_ERROR (errors.idToken/newPassword) or GOOGLE_LINK_REQUIRED; 401 UNAUTHORIZED, INVALID_FIREBASE_TOKEN
    /// (invalid or another Google account) or RECENT_LOGIN_REQUIRED; 409 PASSWORD_ALREADY_SET (use change-password),
    /// ACCOUNT_CHANGED or PASSWORD_RESET_PENDING; 503 GOOGLE_PROVIDER_UNAVAILABLE.</remarks>
    [HttpPost]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Set([FromBody] SetPasswordRequest request, CancellationToken ct)
    {
        var result = await passwords.SetAsync(User.GetActorId(), User.GetSessionFamilyId(), request, ct);
        if (result.IsSuccess) return NoContent();
        var error = result.Error!;
        var problem = new ProblemDetails { Title = error.Message, Status = error.Status };
        problem.Extensions["code"] = error.Code;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        if (error.Errors is not null) problem.Extensions["errors"] = error.Errors;
        return StatusCode(error.Status, problem);
    }
}
