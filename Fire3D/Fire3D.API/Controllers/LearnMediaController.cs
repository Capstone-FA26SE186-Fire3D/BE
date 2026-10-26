using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// Learn images (BE#171): PlatformAdmin uploads a cover or block image through a presigned PUT, then completes it; the returned url is a
/// stable public https address on this API that redirects to a short-lived signed S3 URL, used as coverImageUrl or an image block url.
/// </summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LearnMediaController(ILearnMediaService media, IConfiguration configuration) : ContentControllerBase
{
    /// <summary>Starts an image upload: image/jpeg, image/png or image/webp, at most 5 MB, with the file's sha256. Idempotency-Key required.</summary>
    /// <remarks>PUT the file to uploadUrl with the returned headers before expiresAt (15 minutes). The same key and input returns the same
    /// uploadId; the same key with other input is 409 IDEMPOTENCY_KEY_CONFLICT. 503 LEARN_MEDIA_UNAVAILABLE when storage is down.</remarks>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/learn/media/images/upload-intent")]
    [ProducesResponseType<LearnImageUploadIntentResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> CreateUploadIntent(LearnImageUploadIntentRequest request,
        [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await media.CreateUploadIntentAsync(Actor, key, request, ct);
        return result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error!);
    }

    /// <summary>Checks the uploaded file (size, type, sha256, full decode up to 4096x4096, one frame) and makes it a permanent image.</summary>
    /// <remarks>Completing again returns the same image. 400 VALIDATION_ERROR: size or type differs from the intent; 422 LEARN_IMAGE_INVALID:
    /// the bytes are not that image or not the declared sha256; 409 LEARN_UPLOAD_MISSING, LEARN_UPLOAD_CHANGED or LEARN_UPLOAD_EXPIRED.</remarks>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/learn/media/images/{uploadId:guid}/complete")]
    [ProducesResponseType<LearnImageResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Complete(Guid uploadId, CancellationToken ct)
    {
        var result = await media.CompleteUploadAsync(Actor, uploadId, PublicBaseUrl, ct);
        return result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error!);
    }

    /// <summary>Public, stable address of a completed Learn image: 302 to a signed URL valid 10 minutes; 404 for unknown or unfinished uploads.</summary>
    [AllowAnonymous, HttpGet("api/learn/media/images/{imageId:guid}")]
    [ProducesResponseType(302)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Image(Guid imageId, CancellationToken ct)
    {
        var result = await media.SignedImageUrlAsync(imageId, ct);
        if (!result.IsSuccess) return ToProblem(result.Error!);
        // Browsers may reuse the redirect for 5 minutes, well inside the signed URL's 10.
        Response.Headers.CacheControl = "public, max-age=300";
        return Redirect(result.Value!);
    }

    // The url clients store: LearnMedia:PublicBaseUrl when configured, else this request's host over https.
    private string PublicBaseUrl => configuration["LearnMedia:PublicBaseUrl"] is { Length: > 0 } configured ? configured : $"https://{Request.Host}";

    private ObjectResult ToProblem(AuthError error) => Problem(statusCode: error.Status, title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
}
