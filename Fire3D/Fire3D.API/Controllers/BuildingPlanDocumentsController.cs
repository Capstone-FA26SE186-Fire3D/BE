using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// The signed PC06 plan of a Building (BE#212): a private PDF the head of the facility signed outside FET3D (Điều 15 Nghị định
/// 105/2025/NĐ-CP). The Building's OrganizationUser or PlatformAdmin uploads it through a presigned PUT and completes it; the API checks
/// that it is a PDF, stores it privately and sets signedPlanDocumentId on the Building. Reading returns a short-lived signed URL.
/// </summary>
[ApiController]
[Authorize(Roles = "OrganizationUser,PlatformAdmin")]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BuildingPlanDocumentsController(IBuildingPlanDocuments plans) : ControllerBase
{
    /// <summary>Starts the upload: contentType application/pdf (anything else 400 errors.contentType), at most 20 MB. Idempotency-Key required.</summary>
    /// <remarks>PUT the file to uploadUrl with the returned headers before expiresAt (30 minutes), then complete.</remarks>
    [HttpPost("/api/buildings/{id:guid}/signed-plan/upload-intent")]
    [ProducesResponseType<BuildingPlanUploadIntentResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> CreateUploadIntent(Guid id, BuildingPlanUploadIntentRequest request,
        [FromHeader(Name = "Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
        => Respond(await plans.CreateUploadIntentAsync(User.GetActorId(), id, key, request, ct));

    /// <summary>Checks the uploaded file and makes it the Building's signed plan. Completing again returns the same document.</summary>
    /// <remarks>422 SIGNED_PLAN_NOT_PDF (the bytes are not a PDF); 400 VALIDATION_ERROR (size or type differ from the intent);
    /// 409 SIGNED_PLAN_UPLOAD_MISSING, SIGNED_PLAN_UPLOAD_CHANGED, SIGNED_PLAN_UPLOAD_EXPIRED.</remarks>
    [HttpPost("/api/buildings/{id:guid}/signed-plan/uploads/{uploadId:guid}/complete")]
    [ProducesResponseType<BuildingPlanDocumentResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Complete(Guid id, Guid uploadId, CancellationToken ct)
        => Respond(await plans.CompleteUploadAsync(User.GetActorId(), id, uploadId, ct));

    /// <summary>302 to a 10-minute signed URL of the Building's current signed plan; 404 when it has none.</summary>
    [HttpGet("/api/buildings/{id:guid}/signed-plan")]
    [ProducesResponseType(302)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        var result = await plans.SignedUrlAsync(User.GetActorId(), id, ct);
        return result.IsSuccess ? Redirect(result.Value!) : Problem(result.Error!);
    }

    private IActionResult Respond<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);

    private ObjectResult Problem(AuthError error) => Problem(statusCode: error.Status, title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
}
