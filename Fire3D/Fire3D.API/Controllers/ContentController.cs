using System.Globalization;
using System.Text.Json;
using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// Learn: public reads only return Published posts and their published version; Hidden returns 410 and never content; Deleted and
/// Unpublished 404. PlatformAdmin manages drafts and the post lifecycle; Trainees bookmark. Every lifecycle change writes audit
/// and a PlatformCacheInvalidation outbox event in the same transaction.
/// </summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LearnController(IContentGates gates) : ContentControllerBase
{
    [AllowAnonymous, HttpGet("api/learn/situations")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> Situations(CancellationToken ct) => Reply(await gates.LearnPublicAsync("Situations", null, null, new { }, ct));

    [AllowAnonymous, HttpGet("api/learn/posts")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> Posts([FromQuery] string? situation, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Reply(await gates.LearnPublicAsync("Posts", null, null, new { situation, page, pageSize }, ct));

    [AllowAnonymous, HttpGet("api/learn/posts/{slug}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(410)]
    public async Task<IActionResult> Post(string slug, CancellationToken ct) => Reply(await gates.LearnPublicAsync("Post", null, null, new { slug }, ct));

    /// <summary>Bookmarks of posts that are no longer public are listed as available=false without content.</summary>
    [Authorize(Roles = "Trainee"), HttpGet("api/learn/bookmarks")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> Bookmarks([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Reply(await gates.LearnPublicAsync("Bookmarks", User.GetActorId(), User.GetSessionFamilyId(), new { page, pageSize }, ct));

    [Authorize(Roles = "Trainee"), HttpPut("api/learn/bookmarks/{postId:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Bookmark(Guid postId, CancellationToken ct) =>
        Reply(await gates.LearnPublicAsync("Bookmark", User.GetActorId(), User.GetSessionFamilyId(), new { postId }, ct));

    [Authorize(Roles = "Trainee"), HttpDelete("api/learn/bookmarks/{postId:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> Unbookmark(Guid postId, CancellationToken ct) =>
        Reply(await gates.LearnPublicAsync("Unbookmark", User.GetActorId(), User.GetSessionFamilyId(), new { postId }, ct));

    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpGet("api/admin/learn/posts")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> AdminPosts([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Paging(page, pageSize) ?? Reply(await gates.LearnAdminAsync("List", Actor, Family, Guid.Empty, new { status, page, pageSize }, null, null, ct));

    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpGet("api/admin/learn/posts/{id:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> AdminPost(Guid id, CancellationToken ct) => WithETag(await gates.LearnAdminAsync("Get", Actor, Family, id, new { }, null, null, ct));

    /// <summary>Creates a post with its first Draft version; publish=true publishes it atomically. Idempotency-Key required.</summary>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/learn/posts")]
    [ProducesResponseType<JsonElement>(201)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> CreatePost(CreateLearnPostRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var (issues, content) = LearnContent.Normalize(request.Version);
        if (content is null) return Invalid(issues);
        var result = await gates.LearnAdminAsync("CreatePost", Actor, Family, Guid.Empty, new { slug = request.Slug?.Trim(), version = content, publish = request.Publish }, key, null, ct);
        return result.IsSuccess ? Created($"/api/admin/learn/posts/{result.Value.GetProperty("id").GetGuid()}", result.Value) : Problem(result.Error!);
    }

    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/learn/posts/{id:guid}/versions")]
    [ProducesResponseType<JsonElement>(201)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> CreateVersion(Guid id, LearnVersionInput request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var (issues, content) = LearnContent.Normalize(request);
        if (content is null) return Invalid(issues);
        var result = await gates.LearnAdminAsync("CreateVersion", Actor, Family, id, content, key, null, ct);
        return result.IsSuccess ? Created($"/api/admin/learn/versions/{result.Value.GetProperty("id").GetGuid()}", result.Value) : Problem(result.Error!);
    }

    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpGet("api/admin/learn/versions/{versionId:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Version(Guid versionId, CancellationToken ct) => WithETag(await gates.LearnAdminAsync("GetVersion", Actor, Family, versionId, new { }, null, null, ct));

    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPut("api/admin/learn/versions/{versionId:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> UpdateVersion(Guid versionId, LearnVersionInput request, [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        if (Precondition(ifMatch, out var expected) is { } bad) return bad;
        var (issues, content) = LearnContent.Normalize(request);
        if (content is null) return Invalid(issues);
        return WithETag(await gates.LearnAdminAsync("UpdateVersion", Actor, Family, versionId, content, null, expected, ct));
    }

    /// <summary>Publishes a Draft version (If-Match = version ETag). Cited sources must be approved Common sources.</summary>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/learn/versions/{versionId:guid}/publish")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Publish(Guid versionId, [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct) =>
        Precondition(ifMatch, out var expected) ?? WithETag(await gates.LearnAdminAsync("PublishVersion", Actor, Family, versionId, new { }, null, expected, ct));

    /// <summary>Post lifecycle with If-Match = post ETag: hide (Published→Hidden), show (Hidden→Published), delete, restore (to Hidden when it was ever published, otherwise Unpublished).</summary>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/learn/posts/{id:guid}/{operation:regex(^(hide|show|delete|restore)$)}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> Lifecycle(Guid id, string operation, [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct) =>
        Precondition(ifMatch, out var expected) ?? WithETag(await gates.LearnAdminAsync(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(operation), Actor, Family, id, new { }, null, expected, ct));

    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/learn/situations")]
    [ProducesResponseType<JsonElement>(201)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> CreateSituation(CreateLearnSituationRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await gates.LearnAdminAsync("CreateSituation", Actor, Family, Guid.Empty, request, key, null, ct);
        return result.IsSuccess ? Created("/api/learn/situations", result.Value) : Problem(result.Error!);
    }

    /// <summary>Validates a YouTube, Facebook or TikTok video URL and returns its canonical descriptor. The URL is never fetched.</summary>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/learn/media/validate")]
    [ProducesResponseType<MediaDescriptor>(200)]
    [ProducesResponseType<ProblemDetails>(422)]
    public IActionResult ValidateMedia(MediaValidationRequest request) => LearnContent.Canonicalize(request.Url) is { } media ? Ok(media)
        : Problem(statusCode: 422, title: "Use a full YouTube, Facebook or TikTok video URL.", extensions: new Dictionary<string, object?> { ["code"] = "LEARN_MEDIA_INVALID" });
}

/// <summary>Organization Library: PlatformAdmin maintains versioned templates, rubric samples and equipment metadata; OrganizationUsers read published versions of active items.</summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LibraryController(IContentGates gates) : ContentControllerBase
{
    [Authorize(Roles = "OrganizationUser,PlatformAdmin"), HttpGet("api/library/items")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> Items([FromQuery] string? kind, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Reply(await gates.LibraryAsync("List", Actor, Family, Guid.Empty, new { kind, page, pageSize }, null, null, ct));
    [Authorize(Roles = "OrganizationUser,PlatformAdmin"), HttpGet("api/library/items/{id:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Item(Guid id, CancellationToken ct) => Reply(await gates.LibraryAsync("Get", Actor, Family, id, new { }, null, null, ct));
    [Authorize(Roles = "OrganizationUser,PlatformAdmin"), HttpGet("api/library/versions/{id:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> LibraryVersion(Guid id, CancellationToken ct) => Reply(await gates.LibraryAsync("GetVersion", Actor, Family, id, new { }, null, null, ct));

    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpGet("api/admin/library/items")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> AdminItems([FromQuery] string? kind, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Reply(await gates.LibraryAsync("List", Actor, Family, Guid.Empty, new { kind, page, pageSize, admin = true }, null, null, ct));
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/library/items")]
    [ProducesResponseType<JsonElement>(201)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> CreateItem(CreateLibraryItemRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await gates.LibraryAsync("CreateItem", Actor, Family, Guid.Empty, new { kind = request.Kind, code = request.Code?.Trim().ToUpperInvariant() }, key, null, ct);
        return result.IsSuccess ? Created($"/api/admin/library/items/{result.Value.GetProperty("id").GetGuid()}", result.Value) : Problem(result.Error!);
    }
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpGet("api/admin/library/items/{id:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> AdminItem(Guid id, CancellationToken ct) => WithETag(await gates.LibraryAsync("Get", Actor, Family, id, new { admin = true }, null, null, ct));
    /// <summary>Deactivating an item prevents new selections; published versions referenced by snapshots stay readable.</summary>
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPatch("api/admin/library/items/{id:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> UpdateItem(Guid id, UpdateLibraryItemRequest request, [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct) =>
        Precondition(ifMatch, out var expected) ?? WithETag(await gates.LibraryAsync("UpdateItem", Actor, Family, id, request, null, expected, ct));
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/library/items/{id:guid}/versions")]
    [ProducesResponseType<JsonElement>(201)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> CreateLibraryVersion(Guid id, LibraryVersionInput request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var item = await gates.LibraryAsync("Get", Actor, Family, id, new { admin = true }, null, null, ct);
        if (!item.IsSuccess) return Problem(item.Error!);
        var issues = LibraryContent.Validate(item.Value.GetProperty("kind").GetString()!, request);
        if (issues.Count > 0) return Invalid(issues);
        var result = await gates.LibraryAsync("CreateVersion", Actor, Family, id, new { name = request.Name.Trim(), payload = request.Payload, requiredCapabilities = request.RequiredCapabilities ?? [] }, key, null, ct);
        return result.IsSuccess ? Created($"/api/admin/library/versions/{result.Value.GetProperty("id").GetGuid()}", result.Value) : Problem(result.Error!);
    }
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpGet("api/admin/library/versions/{id:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> AdminVersion(Guid id, CancellationToken ct) => WithETag(await gates.LibraryAsync("GetVersion", Actor, Family, id, new { admin = true }, null, null, ct));
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPut("api/admin/library/versions/{id:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> UpdateLibraryVersion(Guid id, LibraryVersionInput request, [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        if (Precondition(ifMatch, out var expected) is { } bad) return bad;
        var version = await gates.LibraryAsync("GetVersion", Actor, Family, id, new { admin = true }, null, null, ct);
        if (!version.IsSuccess) return Problem(version.Error!);
        var issues = LibraryContent.Validate(version.Value.GetProperty("kind").GetString()!, request);
        if (issues.Count > 0) return Invalid(issues);
        return WithETag(await gates.LibraryAsync("UpdateVersion", Actor, Family, id, new { name = request.Name.Trim(), payload = request.Payload, requiredCapabilities = request.RequiredCapabilities ?? [] }, null, expected, ct));
    }
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministration), HttpPost("api/admin/library/versions/{id:guid}/publish")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> PublishLibraryVersion(Guid id, [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct) =>
        Precondition(ifMatch, out var expected) ?? WithETag(await gates.LibraryAsync("PublishVersion", Actor, Family, id, new { }, null, expected, ct));
}

public abstract class ContentControllerBase : ControllerBase
{
    protected Guid Actor => User.GetActorId();
    protected Guid Family => User.GetSessionFamilyId();
    protected IActionResult Reply(AuthResult<JsonElement> result) => result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    // ETag is the quoted revision of the returned resource (post, version or item).
    protected IActionResult WithETag(AuthResult<JsonElement> result)
    {
        if (!result.IsSuccess) return Problem(result.Error!);
        if (result.Value.TryGetProperty("revision", out var revision)) Response.Headers.ETag = $"\"{revision.GetInt64()}\"";
        return Ok(result.Value);
    }
    protected IActionResult? Precondition(string? value, out long expected)
    {
        expected = 0;
        if (string.IsNullOrWhiteSpace(value)) return Problem(statusCode: 428, title: "If-Match is required.", extensions: new Dictionary<string, object?> { ["code"] = "PRECONDITION_REQUIRED" });
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' || !long.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out expected) || expected < 1)
            return Problem(statusCode: 400, title: "If-Match must be the quoted revision from the ETag.", extensions: new Dictionary<string, object?> { ["code"] = "INVALID_IF_MATCH" });
        return null;
    }
    protected IActionResult? Paging(int page, int pageSize) => page >= 1 && pageSize is >= 1 and <= 100 ? null
        : Problem(statusCode: 400, title: "page >= 1 and pageSize 1-100.", extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
    protected ObjectResult Invalid(IReadOnlyList<Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ScenarioDraftValidationIssue> issues) =>
        Problem(statusCode: 422, title: "Content does not satisfy its schema.", extensions: new Dictionary<string, object?> { ["code"] = "CONTENT_INVALID", ["issues"] = issues });
    protected ObjectResult Problem(AuthError error) => Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> { ["code"] = error.Code });
}
