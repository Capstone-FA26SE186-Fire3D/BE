using Fire3D.API.Authorization;
using Fire3D.Application.Ai;
using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// Knowledge source files (BE#179): PlatformAdmin uploads the document through a presigned PUT, then completes it with the source
/// details; the API computes the SHA-256 and registers a Draft source whose sourceUri is the file's stable public address on this
/// API, which redirects to a short-lived signed S3 URL. Registering by URL and hash (POST /api/admin/knowledge-sources) still works.
/// </summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class KnowledgeSourceFilesController(IKnowledgeSourceFileService files, IConfiguration configuration) : ContentControllerBase
{
    /// <summary>Starts a file upload: application/pdf, text/markdown, text/plain or text/html, at most 20 MB. Idempotency-Key required.</summary>
    /// <remarks>PUT the file to uploadUrl with the returned headers before expiresAt (30 minutes). The same key and input returns the same
    /// uploadId; the same key with other input is 409 IDEMPOTENCY_KEY_CONFLICT. 503 KNOWLEDGE_SOURCE_STORAGE_UNAVAILABLE when storage is down.</remarks>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/knowledge-sources/upload-intent")]
    [ProducesResponseType<KnowledgeSourceUploadIntentResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> CreateUploadIntent(KnowledgeSourceUploadIntentRequest request,
        [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await files.CreateUploadIntentAsync(Actor, key, request, ct);
        return result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error!);
    }

    /// <summary>Checks the uploaded file, stores it for good and registers it as a Draft knowledge source with the computed sourceHash.</summary>
    /// <remarks>The body is the source version: visibility, title, versionLabel, organizationId (Organization sources only), jurisdiction,
    /// effectiveFrom, effectiveUntil. Completing again with the same body returns the same source; with another body 409 IDEMPOTENCY_KEY_CONFLICT.
    /// 400 VALIDATION_ERROR: size or type differs from the intent; 422 KNOWLEDGE_SOURCE_FILE_INVALID: not a PDF, or text that is not UTF-8;
    /// 409 KNOWLEDGE_SOURCE_UPLOAD_MISSING, KNOWLEDGE_SOURCE_UPLOAD_CHANGED, KNOWLEDGE_SOURCE_UPLOAD_EXPIRED or KNOWLEDGE_SOURCE_EXISTS
    /// (that file and versionLabel are already registered).</remarks>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/knowledge-sources/uploads/{uploadId:guid}/complete")]
    [ProducesResponseType<System.Text.Json.JsonElement>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Complete(Guid uploadId, CompleteKnowledgeSourceUploadRequest request, CancellationToken ct)
    {
        var issues = OrganizationAiInput.SourceRegistration(request.Visibility, request.Title, request.VersionLabel, request.OrganizationId,
            request.Jurisdiction, request.EffectiveFrom, request.EffectiveUntil);
        if (issues.Count > 0) return Invalid(issues);
        var result = await files.CompleteUploadAsync(Actor, Family, uploadId, request, PublicBaseUrl, ct);
        return result.IsSuccess ? Created($"/api/admin/knowledge-sources/{result.Value.GetProperty("id").GetGuid()}", result.Value) : ToProblem(result.Error!);
    }

    /// <summary>Public, stable address of a registered source file (the sourceUri the AI service fetches): 302 to a signed URL valid 10 minutes.</summary>
    /// <remarks>404 for unknown files and uploads that never registered a source. Files of retired sources stay readable.</remarks>
    [AllowAnonymous, HttpGet("api/knowledge-sources/files/{fileId:guid}")]
    [ProducesResponseType(302)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> SourceFile(Guid fileId, CancellationToken ct)
    {
        var result = await files.SignedFileUrlAsync(fileId, ct);
        if (!result.IsSuccess) return ToProblem(result.Error!);
        // Clients may reuse the redirect for 5 minutes, well inside the signed URL's 10.
        Response.Headers.CacheControl = "public, max-age=300";
        return Redirect(result.Value!);
    }

    // The address stored as sourceUri: LearnMedia:PublicBaseUrl (the API's public origin) when configured, else this request's host over https.
    private string PublicBaseUrl => configuration["LearnMedia:PublicBaseUrl"] is { Length: > 0 } configured ? configured : $"https://{Request.Host}";

    private ObjectResult ToProblem(AuthError error) => Problem(statusCode: error.Status, title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
}
