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
    /// <summary>Sources the organization may pin now, with the link the client opens for each (Learn slug, Library item, document URL).</summary>
    [HttpGet("api/ai/organization/sources")]
    [ProducesResponseType<AiSourceList>(200)]
    public async Task<IActionResult> Sources(CancellationToken ct) => Reply(await gates.OrganizationAsync("Sources", Actor, Family, Guid.Empty, new { }, null, ct));

    /// <summary>What one request of each operation reserves now (estimatedReserve) and whether the organization balance covers it.</summary>
    /// <remarks>policyActive=false means submit returns 503 AI_POLICY_UNAVAILABLE; canSubmit=false with an active policy means 409 AI_QUOTA_EXHAUSTED.</remarks>
    [HttpGet("api/ai/organization/limits")]
    [ProducesResponseType<AiLimitsView>(200)]
    public async Task<IActionResult> Limits(CancellationToken ct) => Reply(await gates.OrganizationAsync("Limits", Actor, Family, Guid.Empty, new { }, null, ct));

    /// <summary>Queues a scenario draft suggestion for a Building of the caller's organization. Idempotency-Key required; returns 202 with the request.</summary>
    /// <remarks>The request pins one revision with accepted geometry (revisionId, or the newest such revision) and sends its BIM facts:
    /// floors, semantic anchors, annotations, quality flags and the active runtime capabilities. No such revision: 409 AI_REVISION_NOT_READY.
    /// A finished draft has status Succeeded, resultType ScenarioDraft and result.status NeedsUserEdit: it must be edited and saved in the editor.</remarks>
    [HttpPost("api/ai/organization/scenario-draft")]
    [ProducesResponseType<AiRequestView>(202)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(503)]
    public Task<IActionResult> ScenarioDraft(ScenarioDraftAiRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var issues = OrganizationAiInput.Text(request.Prompt, "$.prompt");
        if (request.BuildingId == Guid.Empty) issues.Add(new("FIELD_REQUIRED", "$.buildingId", "Required."));
        if (request.RevisionId == Guid.Empty) issues.Add(new("FIELD_INVALID", "$.revisionId", "Omit it or send a revision of the Building."));
        issues.AddRange(OrganizationAiInput.Sources(request.Sources));
        return Submit("ScenarioDraft", new { buildingId = request.BuildingId, revisionId = request.RevisionId, prompt = request.Prompt, sources = request.Sources ?? [] }, issues, key, ct);
    }

    /// <summary>Queues a grounded answer over the caller's eligible sources. Idempotency-Key required; returns 202 with the request.</summary>
    [HttpPost("api/ai/organization/answer")]
    [ProducesResponseType<AiRequestView>(202)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(503)]
    public Task<IActionResult> Answer(AnswerAiRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var issues = OrganizationAiInput.Text(request.Question, "$.question");
        issues.AddRange(OrganizationAiInput.Sources(request.Sources));
        return Submit("Answer", new { question = request.Question, sources = request.Sources ?? [] }, issues, key, ct);
    }

    /// <summary>The caller's organization AI requests, newest first, with the same fields as the detail.</summary>
    /// <remarks>Filters: status (Accepted, Processing, Succeeded, Failed, NeedsReconcile, Rejected), resultType (KnowledgeAnswer, ScenarioDraft,
    /// InsufficientEvidence, RejectedBySafetyGate), operation (ScenarioDraft, Answer), mine=true for the caller's own requests.
    /// page defaults to 1, pageSize to 20 (maximum 100). Other organizations never appear. Each item carries input (text, Building, revision,
    /// inputHash) so a request lost on timeout can be found again.</remarks>
    [HttpGet("api/ai/requests")]
    [ProducesResponseType<AiRequestPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? resultType, [FromQuery] string? operation, [FromQuery] bool? mine, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Paging(page, pageSize) ?? Reply(await gates.OrganizationRequestsAsync(Actor, Family, new { status, resultType, operation, mine, page, pageSize }, ct));

    [HttpGet("api/ai/requests/{requestId:guid}")]
    [ProducesResponseType<AiRequestView>(200)]
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

/// <summary>Trainee AI: answers about Learn, a playable Training or the Trainee's own result, within an Admin-set daily allowance.</summary>
[ApiController]
[Authorize(Roles = "Trainee")]
[Produces("application/json")]
public sealed class TraineeAiController(IOrganizationAiGates gates, IOptions<OrganizationAiOptions> options) : ContentControllerBase
{
    /// <summary>Today's allowance (Asia/Ho_Chi_Minh day), what one question reserves, and whether an open Assessment attempt blocks AI.</summary>
    [HttpGet("api/ai/trainee/quota")]
    [ProducesResponseType<TraineeAiQuotaView>(200)]
    public async Task<IActionResult> Quota(CancellationToken ct) => Reply(await gates.TraineeAsync("Quota", Actor, Family, Guid.Empty, new { }, null, ct));

    /// <summary>Queues a grounded answer for the Trainee. Idempotency-Key required; returns 202 with the request.</summary>
    /// <remarks>context.kind Learn needs postId and versionId of the current published version; Training needs a trainingId the Trainee may play
    /// (Public Building or valid participation grant, Active Training, Published release); Result needs the Trainee's own sessionId with a saved result.
    /// Unavailable context: 404 AI_CONTEXT_NOT_FOUND. Sources: approved Common knowledge sources and Learn posts only (Hidden posts are used
    /// for retrieval without being listed). Blocked while an Assessment attempt is open: 409 AI_BLOCKED_DURING_ASSESSMENT. Today's allowance used
    /// up: 409 AI_DAILY_QUOTA_EXHAUSTED; a retry with the same key never reserves twice. Rubric, scoring, drafts and private IFC never reach the AI.</remarks>
    [HttpPost("api/ai/trainee/answer")]
    [ProducesResponseType<AiRequestView>(202)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Answer(TraineeAiRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var issues = OrganizationAiInput.Text(request.Question, "$.question");
        issues.AddRange(OrganizationAiInput.Sources(request.Sources, OrganizationAiInput.TraineeSourceKinds));
        var context = request.Context;
        if (context is not null && !(context.Kind switch
            {
                "Learn" => context.PostId is { } p && p != Guid.Empty && context.VersionId is { } v && v != Guid.Empty,
                "Training" => context.TrainingId is { } t && t != Guid.Empty,
                "Result" => context.SessionId is { } s && s != Guid.Empty,
                _ => false
            }))
            issues.Add(new("FIELD_INVALID", "$.context", "Learn needs postId and versionId, Training needs trainingId, Result needs sessionId."));
        if (issues.Count > 0) return Invalid(issues);
        if (!options.Value.Configured)
            return Problem(statusCode: 503, title: "The AI service is not configured.", extensions: new Dictionary<string, object?> { ["code"] = "AI_PROVIDER_UNAVAILABLE" });
        var result = await gates.TraineeAsync("Submit", Actor, Family, Guid.Empty, new
        {
            question = request.Question, sources = request.Sources ?? [],
            context = context is null ? null : new { kind = context.Kind, postId = context.PostId, versionId = context.VersionId, trainingId = context.TrainingId, sessionId = context.SessionId }
        }, key, ct);
        return result.IsSuccess ? Accepted($"/api/ai/trainee/requests/{result.Value.GetProperty("requestId").GetGuid()}", result.Value) : Problem(result.Error!);
    }

    /// <summary>The Trainee's own AI requests, newest first; status filter as in the organization list.</summary>
    /// <remarks>resultWithheld=true: the context is no longer available (Building access lost, Learn post removed), so the answer and citations are hidden.</remarks>
    [HttpGet("api/ai/trainee/requests")]
    [ProducesResponseType<AiRequestPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Paging(page, pageSize) ?? Reply(await gates.TraineeAsync("List", Actor, Family, Guid.Empty, new { status, page, pageSize }, null, ct));

    [HttpGet("api/ai/trainee/requests/{requestId:guid}")]
    [ProducesResponseType<AiRequestView>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken ct) => Reply(await gates.TraineeAsync("Get", Actor, Family, requestId, new { }, null, ct));
}

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministration)]
[Produces("application/json")]
public sealed partial class AdminAiController(IOrganizationAiGates gates, Fire3D.Application.Content.IContentGates content) : ContentControllerBase
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")] private static partial Regex VersionLabel();

    /// <summary>All AI requests, newest first. Filters: status, operation (ScenarioDraft, Answer, TraineeAnswer), audience (organization, trainee), organizationId.</summary>
    [HttpGet("api/admin/ai/requests")]
    [ProducesResponseType<AdminAiRequestPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> Requests([FromQuery] string? status, [FromQuery] string? operation, [FromQuery] string? audience, [FromQuery] Guid? organizationId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Paging(page, pageSize) ?? Reply(await gates.AdminAsync("ListRequests", Actor, Family, Guid.Empty, new { status, operation, audience, organizationId, page, pageSize }, null, null, ct));

    [HttpGet("api/admin/ai/requests/{requestId:guid}")]
    [ProducesResponseType<AdminAiRequestView>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> AiRequest(Guid requestId, CancellationToken ct) => Reply(await gates.AdminAsync("GetRequest", Actor, Family, requestId, new { }, null, null, ct));

    /// <summary>Schedules a provider lookup for a NeedsReconcile request. Never edits consumed usage. Idempotency-Key required.</summary>
    [HttpPost("api/admin/ai/requests/{requestId:guid}/reconcile")]
    [ProducesResponseType<AdminAiRequestView>(202)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Reconcile(Guid requestId, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await gates.AdminAsync("Reconcile", Actor, Family, requestId, new { }, key, null, ct);
        return result.IsSuccess ? Accepted($"/api/admin/ai/requests/{requestId}", result.Value) : Problem(result.Error!);
    }

    /// <summary>AI policies by operation and newest period first; page defaults to 1, pageSize to 20 (maximum 100).</summary>
    [HttpGet("api/admin/ai/policies")]
    [ProducesResponseType<AiPolicyPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> Policies([FromQuery] string? operation, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Paging(page, pageSize) ?? Reply(await gates.AdminAsync("ListPolicies", Actor, Family, Guid.Empty, new { operation, page, pageSize }, null, null, ct));

    /// <summary>Creates an immutable reservation policy for one operation; periods of one operation may not overlap. Idempotency-Key required.</summary>
    /// <remarks>TraineeAnswer also needs dailyUnits (1-100000000, at least reserveUnits): the units one Trainee may use per Vietnam calendar day.</remarks>
    [HttpPost("api/admin/ai/policies")]
    [ProducesResponseType<AiPolicyView>(201)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> CreatePolicy(CreateAiPolicyRequest request, [FromHeader(Name = "Idempotency-Key"), OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (request.Operation is not ("ScenarioDraft" or "Answer" or "TraineeAnswer")) issues.Add(new("VALUE_UNSUPPORTED", "$.operation", "ScenarioDraft, Answer or TraineeAnswer."));
        if (request.Operation == "TraineeAnswer" && (request.DailyUnits is not (>= 1 and <= 100_000_000) || request.DailyUnits < request.ReserveUnits))
            issues.Add(new("VALUE_OUT_OF_RANGE", "$.dailyUnits", "1-100000000 and at least reserveUnits."));
        if (request.Operation != "TraineeAnswer" && request.DailyUnits is not null) issues.Add(new("FIELD_INVALID", "$.dailyUnits", "Only TraineeAnswer has a daily allowance."));
        if (!OrganizationAiInput.QuotaUnit().IsMatch(request.QuotaUnit ?? "")) issues.Add(new("VALUE_UNSUPPORTED", "$.quotaUnit", "Lower-case quota unit of the billing policy."));
        if (request.ReserveUnits is < 1 or > 100_000_000) issues.Add(new("VALUE_OUT_OF_RANGE", "$.reserveUnits", "1-100000000."));
        if (request.MaxInputChars is < 1 or > 20_000) issues.Add(new("VALUE_OUT_OF_RANGE", "$.maxInputChars", "1-20000."));
        if (request.MaxSources is < 1 or > 100) issues.Add(new("VALUE_OUT_OF_RANGE", "$.maxSources", "1-100."));
        if (request.EffectiveUntil <= (request.EffectiveFrom ?? DateTimeOffset.UtcNow)) issues.Add(new("VALUE_OUT_OF_RANGE", "$.effectiveUntil", "Must be after effectiveFrom."));
        if (issues.Count > 0) return Invalid(issues);
        var result = await gates.AdminAsync("CreatePolicy", Actor, Family, Guid.Empty, request, key, null, ct);
        return result.IsSuccess ? Created("/api/admin/ai/policies", result.Value) : Problem(result.Error!);
    }

    /// <summary>Knowledge source registry and editor catalog. Without visibility every source is listed (admin review); the Learn editor passes visibility=Common so tenant sources never enter its catalog. status Draft/Approved/Retired; search on title, version label and jurisdiction. canUseInDraft: a Learn draft may cite it; canPublishReference: publish/Hidden/RAG accept it now.</summary>
    [HttpGet("api/admin/knowledge-sources")]
    [ProducesResponseType<Fire3D.Application.Content.KnowledgeSourcePage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    public async Task<IActionResult> KnowledgeSources([FromQuery] string? visibility, [FromQuery] string? status, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (Paging(page, pageSize) is { } paging) return paging;
        if (visibility is not (null or "Common" or "Organization") || status is not (null or "Draft" or "Approved" or "Retired") || search is { Length: > 200 })
            return Problem(statusCode: 400, title: "Use visibility Common/Organization, status Draft/Approved/Retired and search up to 200 characters.", extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
        return Reply(await content.CatalogAsync("KnowledgeSources", Actor, Family, new { visibility, status, search, page, pageSize }, ct));
    }

    [HttpGet("api/admin/knowledge-sources/{id:guid}")]
    [ProducesResponseType<Fire3D.Application.Content.KnowledgeSourceView>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> KnowledgeSource(Guid id, CancellationToken ct)
    {
        // Same gate and flag rule as the list, so a source picked earlier can be reopened by ID after a reload.
        var result = await content.CatalogAsync("KnowledgeSources", Actor, Family, new { id, page = 1, pageSize = 1 }, ct);
        if (!result.IsSuccess) return Problem(result.Error!);
        var items = result.Value.GetProperty("items");
        if (items.GetArrayLength() == 0) return Problem(new AuthError("NOT_FOUND", "The knowledge source was not found.", 404));
        return WithETag(AuthResult<JsonElement>.Ok(items[0].Clone()));
    }

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
        Precondition(ifMatch, out var expected) ?? WithETag(await gates.AdminAsync(LifecycleAction(operation), Actor, Family, id, new { }, null, expected, ct));
}
