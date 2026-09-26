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
    public async Task<ActionResult<AvatarResponse>> Complete(CompleteAvatarUploadRequest request, CancellationToken ct)
    {
        var result = await avatars.CompleteUploadAsync(User.GetActorId(), request, ct);
        return result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error!);
    }

    [HttpGet]
    [ProducesResponseType<AvatarResponse>(200)]
    public async Task<ActionResult<AvatarResponse>> Get(CancellationToken ct)
    {
        var result = await avatars.GetAvatarAsync(User.GetActorId(), ct);
        return result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error!);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(CancellationToken ct)
    {
        var result = await avatars.DeleteAvatarAsync(User.GetActorId(), ct);
        return result.IsSuccess ? NoContent() : ToProblem(result.Error!);
    }

    private ObjectResult ToProblem(AuthError error) => Problem(statusCode: error.Status, title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code });
}
