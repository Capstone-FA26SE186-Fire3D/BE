using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Avatar;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/me/avatar")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AvatarController(IAvatarService avatars) : ControllerBase
{
    [HttpPost("upload-intent")]
    [ProducesResponseType<AvatarUploadIntentResponse>(200)]
    public async Task<ActionResult<AvatarUploadIntentResponse>> CreateUploadIntent(AvatarUploadIntentRequest request, CancellationToken ct)
    {
        var result = await avatars.CreateUploadIntentAsync(User.GetActorId(), request, ct);
        return result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error!);
    }

    [HttpPost("complete")]
    [ProducesResponseType<AvatarResponse>(200)]
    public async Task<ActionResult<AvatarResponse>> Complete(CompleteAvatarUploadRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct)
    {
        if (!ProfileEtag.TryParse(ifMatch, out var revision)) return EtagProblem(ifMatch);
        var result = await avatars.CompleteUploadAsync(User.GetActorId(), revision, request, ct);
        if (!result.IsSuccess) return ToProblem(result.Error!);
        Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
        return Ok(result.Value);
    }

    [HttpGet]
    [ProducesResponseType<AvatarResponse>(200)]
    public async Task<ActionResult<AvatarResponse>> Get(CancellationToken ct)
    {
        var result = await avatars.GetAvatarAsync(User.GetActorId(), ct);
        if (!result.IsSuccess) return ToProblem(result.Error!);
        Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
        return Ok(result.Value);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete([FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct)
    {
        if (!ProfileEtag.TryParse(ifMatch, out var revision)) return EtagProblem(ifMatch);
        var result = await avatars.DeleteAvatarAsync(User.GetActorId(), revision, ct);
        if (!result.IsSuccess) return ToProblem(result.Error!);
        Response.Headers.ETag = ProfileEtag.Format(revision + 1);
        return NoContent();
    }

    private ObjectResult ToProblem(AuthError error) => Problem(statusCode: error.Status, title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    private ObjectResult EtagProblem(string? ifMatch) => Problem(
        statusCode: string.IsNullOrWhiteSpace(ifMatch) ? StatusCodes.Status428PreconditionRequired : StatusCodes.Status400BadRequest,
        title: string.IsNullOrWhiteSpace(ifMatch) ? "Send the ETag from GET /api/auth/me in If-Match." : "If-Match must contain one quoted positive revision.",
        extensions: new Dictionary<string, object?> { ["code"] = string.IsNullOrWhiteSpace(ifMatch) ? "PRECONDITION_REQUIRED" : "VALIDATION_ERROR" });
}
