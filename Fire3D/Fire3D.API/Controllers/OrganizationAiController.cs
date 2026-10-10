using System.Text.Json;
using System.Text.RegularExpressions;
using Fire3D.API.Authorization;
using Fire3D.Application.Ai;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Fire3D.API.Controllers;

/// <summary>Organization AI: asynchronous requests answered by the FastAPI adapter; results are drafts only and never change scenarios.</summary>
[ApiController]
[Authorize(Roles = "OrganizationUser")]
[Produces("application/json")]
public sealed class OrganizationAiController(IOrganizationAiGates gates, IOptions<OrganizationAiOptions> options) : ContentControllerBase
{
    [HttpGet("api/ai/organization/sources")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> Sources(CancellationToken ct) => Reply(await gates.OrganizationAsync("Sources", Actor, Family, Guid.Empty, new { }, null, ct));

    /// <summary>Queues a scenario draft suggestion for a Building of the caller's organization. Idempotency-Key required; returns 202 with the request.</summary>
    [HttpPost("api/ai/organization/scenario-draft")]
    [ProducesResponseType<JsonElement>(202)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(503)]
    public Task<IActionResult> ScenarioDraft(ScenarioDraftAiRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var issues = OrganizationAiInput.Text(request.Prompt, "$.prompt");
        if (request.BuildingId == Guid.Empty) issues.Add(new("FIELD_REQUIRED", "$.buildingId", "Required."));
        issues.AddRange(OrganizationAiInput.Sources(request.Sources));
        return Submit("ScenarioDraft", new { buildingId = request.BuildingId, prompt = request.Prompt, sources = request.Sources ?? [] }, issues, key, ct);
    }

    /// <summary>Queues a grounded answer over the caller's eligible sources. Idempotency-Key required; returns 202 with the request.</summary>
    [HttpPost("api/ai/organization/answer")]
    [ProducesResponseType<JsonElement>(202)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(503)]
    public Task<IActionResult> Answer(AnswerAiRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var issues = OrganizationAiInput.Text(request.Question, "$.question");
        issues.AddRange(OrganizationAiInput.Sources(request.Sources));
        return Submit("Answer", new { question = request.Question, sources = request.Sources ?? [] }, issues, key, ct);
    }

    [HttpGet("api/ai/requests/{requestId:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken ct) => Reply(await gates.OrganizationAsync("Get", Actor, Family, requestId, new { }, null, ct));

    private async Task<IActionResult> Submit(string operation, object request, List<ScenarioDraftValidationIssue> issues, string? key, CancellationToken ct)
    {
        if (issues.Count > 0) return Invalid(issues);
        if (!options.Value.Configured)
            return Problem(statusCode: 503, title: "The AI service is not configured.", extensions: new Dictionary<string, object?> { ["code"] = "AI_PROVIDER_UNAVAILABLE" });
        var result = await gates.OrganizationAsync("Submit", Actor, Family, Guid.Empty, new { operation, request }, key, ct);
        return result.IsSuccess ? Accepted($"/api/ai/requests/{result.Value.GetProperty("requestId").GetGuid()}", result.Value) : Problem(result.Error!);
    }
}

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministration)]
[Produces("application/json")]
public sealed partial class AdminAiController(IOrganizationAiGates gates) : ContentControllerBase
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")] private static partial Regex VersionLabel();

    [HttpGet("api/admin/ai/requests")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> Requests([FromQuery] string? status, [FromQuery] Guid? organizationId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Paging(page, pageSize) ?? Reply(await gates.AdminAsync("ListRequests", Actor, Family, Guid.Empty, new { status, organizationId, page, pageSize }, null, null, ct));

    [HttpGet("api/admin/ai/requests/{requestId:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> AiRequest(Guid requestId, CancellationToken ct) => Reply(await gates.AdminAsync("GetRequest", Actor, Family, requestId, new { }, null, null, ct));

    /// <summary>Schedules a provider lookup for a NeedsReconcile request. Never edits consumed usage. Idempotency-Key required.</summary>
    [HttpPost("api/admin/ai/requests/{requestId:guid}/reconcile")]
    [ProducesResponseType<JsonElement>(202)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Reconcile(Guid requestId, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await gates.AdminAsync("Reconcile", Actor, Family, requestId, new { }, key, null, ct);
        return result.IsSuccess ? Accepted($"/api/admin/ai/requests/{requestId}", result.Value) : Problem(result.Error!);
    }

    [HttpGet("api/admin/ai/policies")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> Policies(CancellationToken ct) => Reply(await gates.AdminAsync("ListPolicies", Actor, Family, Guid.Empty, new { }, null, null, ct));

    /// <summary>Creates an immutable reservation policy for one operation; periods of one operation may not overlap. Idempotency-Key required.</summary>
    [HttpPost("api/admin/ai/policies")]
    [ProducesResponseType<JsonElement>(201)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> CreatePolicy(CreateAiPolicyRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (request.Operation is not ("ScenarioDraft" or "Answer")) issues.Add(new("VALUE_UNSUPPORTED", "$.operation", "ScenarioDraft or Answer."));
        if (!OrganizationAiInput.QuotaUnit().IsMatch(request.QuotaUnit ?? "")) issues.Add(new("VALUE_UNSUPPORTED", "$.quotaUnit", "Lower-case quota unit of the billing policy."));
        if (request.ReserveUnits is < 1 or > 100_000_000) issues.Add(new("VALUE_OUT_OF_RANGE", "$.reserveUnits", "1-100000000."));
        if (request.MaxInputChars is < 1 or > 20_000) issues.Add(new("VALUE_OUT_OF_RANGE", "$.maxInputChars", "1-20000."));
        if (request.MaxSources is < 1 or > 100) issues.Add(new("VALUE_OUT_OF_RANGE", "$.maxSources", "1-100."));
        if (request.EffectiveUntil <= (request.EffectiveFrom ?? DateTimeOffset.UtcNow)) issues.Add(new("VALUE_OUT_OF_RANGE", "$.effectiveUntil", "Must be after effectiveFrom."));
        if (issues.Count > 0) return Invalid(issues);
        var result = await gates.AdminAsync("CreatePolicy", Actor, Family, Guid.Empty, request, key, null, ct);
        return result.IsSuccess ? Created("/api/admin/ai/policies", result.Value) : Problem(result.Error!);
    }

    [HttpGet("api/admin/knowledge-sources")]
    [ProducesResponseType<JsonElement>(200)]
    public async Task<IActionResult> KnowledgeSources([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Paging(page, pageSize) ?? Reply(await gates.AdminAsync("ListSources", Actor, Family, Guid.Empty, new { status, page, pageSize }, null, null, ct));

    [HttpGet("api/admin/knowledge-sources/{id:guid}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> KnowledgeSource(Guid id, CancellationToken ct) => WithETag(await gates.AdminAsync("GetSource", Actor, Family, id, new { }, null, null, ct));

    /// <summary>Registers one source version as Draft. Common sources have no organization; Organization sources belong to one tenant. Idempotency-Key required.</summary>
    [HttpPost("api/admin/knowledge-sources")]
    [ProducesResponseType<JsonElement>(201)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> CreateKnowledgeSource(CreateKnowledgeSourceRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var issues = OrganizationAiInput.Text(request.Title, "$.title", 500);
        if (request.Visibility is not ("Common" or "Organization")) issues.Add(new("VALUE_UNSUPPORTED", "$.visibility", "Common or Organization."));
        if ((request.Visibility == "Organization") != (request.OrganizationId is not null)) issues.Add(new("FIELD_INVALID", "$.organizationId", "Required for Organization sources only."));
        if (!VersionLabel().IsMatch(request.VersionLabel ?? "")) issues.Add(new("VALUE_UNSUPPORTED", "$.versionLabel", "1-100 of letters, digits, dot, dash or underscore."));
        if (!OrganizationAiInput.Sha256().IsMatch(request.SourceHash?.ToLowerInvariant() ?? "")) issues.Add(new("VALUE_UNSUPPORTED", "$.sourceHash", "Lower-case SHA-256 hex of the source document."));
        if (request.SourceUri is not null && (!Uri.TryCreate(request.SourceUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)))
            issues.Add(new("URL_INVALID", "$.sourceUri", "Absolute https URL without credentials."));
        if (request.Jurisdiction is not null) issues.AddRange(OrganizationAiInput.Text(request.Jurisdiction, "$.jurisdiction", 100));
        if (request.EffectiveFrom is not null && request.EffectiveUntil <= request.EffectiveFrom) issues.Add(new("VALUE_OUT_OF_RANGE", "$.effectiveUntil", "Must be after effectiveFrom."));
        if (issues.Count > 0) return Invalid(issues);
        var result = await gates.AdminAsync("CreateSource", Actor, Family, Guid.Empty, request, key, null, ct);
        return result.IsSuccess ? Created($"/api/admin/knowledge-sources/{result.Value.GetProperty("id").GetGuid()}", result.Value) : Problem(result.Error!);
    }

    [HttpPost("api/admin/knowledge-sources/{id:guid}/{operation:regex(^(approve|retire)$)}")]
    [ProducesResponseType<JsonElement>(200)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> ChangeKnowledgeSource(Guid id, string operation, [FromHeader(Name = "If-Match"), OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct) =>
        Precondition(ifMatch, out var expected) ?? WithETag(await gates.AdminAsync(operation == "approve" ? "ApproveSource" : "RetireSource", Actor, Family, id, new { }, null, expected, ct));
}
