using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;

namespace Fire3D.Application.Ai;

public sealed record AiSourceRef(string Kind, Guid Id);
/// <summary>RevisionId pins the Building revision whose accepted geometry the draft uses; omitted, the newest revision with accepted geometry.</summary>
public sealed record ScenarioDraftAiRequest(Guid BuildingId, string Prompt, IReadOnlyList<AiSourceRef>? Sources = null, Guid? RevisionId = null);
public sealed record AnswerAiRequest(string Question, IReadOnlyList<AiSourceRef>? Sources = null);
/// <summary>DailyUnits is required for TraineeAnswer (units one Trainee may use per Vietnam calendar day) and absent otherwise.</summary>
public sealed record CreateAiPolicyRequest(string Operation, string QuotaUnit, int ReserveUnits, int MaxInputChars, int MaxSources, DateTimeOffset? EffectiveFrom = null, DateTimeOffset? EffectiveUntil = null, int? DailyUnits = null);
/// <summary>EffectiveUntil defaults to now; it must not be in the past, must be after effectiveFrom and earlier than the current end.</summary>
public sealed record EndAiPolicyRequest(DateTimeOffset? EffectiveUntil = null);
/// <summary>Kind Learn (PostId + VersionId of the published version), Training (TrainingId the Trainee may play) or Result (own SessionId).</summary>
public sealed record TraineeAiContext(string Kind, Guid? PostId = null, Guid? VersionId = null, Guid? TrainingId = null, Guid? SessionId = null);
public sealed record TraineeAiRequest(string Question, TraineeAiContext? Context = null, IReadOnlyList<AiSourceRef>? Sources = null);
public sealed record CreateKnowledgeSourceRequest(string Visibility, string Title, string VersionLabel, string SourceHash, Guid? OrganizationId = null,
    string? SourceUri = null, string? Jurisdiction = null, DateTimeOffset? EffectiveFrom = null, DateTimeOffset? EffectiveUntil = null);

// Response shapes of the AI gates (#89), declared so OpenAPI describes what the SQL representations return.
// Vocabulary follows Docs v7: status Accepted | Processing | Succeeded | Failed | NeedsReconcile;
// resultType KnowledgeAnswer | ScenarioDraft | InsufficientEvidence | RejectedBySafetyGate;
// result.status Answered | NeedsUserEdit | InsufficientEvidence | RejectedBySafetyGate.
/// <summary>Where the client opens a source: Learn post slug, Library item, or the knowledge source document URL.</summary>
public sealed record AiSourceLink(string? LearnSlug, Guid? LearnPostId, Guid? LibraryItemId, string? SourceUri);
public sealed record AiSourceView(string Kind, Guid Id, Guid VersionId, string Title, string VersionLabel, AiSourceLink Link);
public sealed record AiSourceList(IReadOnlyList<AiSourceView> Items);
public sealed record AiPinnedSourceView(string Kind, Guid Id, Guid VersionId, string Title, string VersionLabel, string ContentHash, AiSourceLink Link);
/// <summary>A citation resolved against the pinned sources: kind/id/version/title come from the pin, never from the provider.</summary>
public sealed record AiCitationView(string Kind, Guid Id, Guid VersionId, string Title, string VersionLabel, string? ChunkId, string? Locator, double? Relevance, AiSourceLink Link);
public sealed record AiBimAnchorView(string IfcGlobalId, string? EntityType, Guid RevisionId);
public sealed record AiDraftView(JsonElement State, string? Notes);
public sealed record AiRequestResultView(string Status, string? Answer, AiDraftView? Draft, string? Message, string? Confidence,
    IReadOnlyList<string>? Assumptions, IReadOnlyList<string>? Limitations, IReadOnlyList<AiBimAnchorView>? BimAnchors);
/// <summary>What was asked: the text, the Building/revision for drafts or the Trainee context, and the canonical input hash.</summary>
public sealed record AiRequestInputView(string Text, string InputHash, Guid? BuildingId, string? BuildingName, Guid? RevisionId,
    IReadOnlyList<AiSourceRef> RequestedSources, JsonElement? Context);
public sealed record AiRequestView(Guid RequestId, string Audience, string Operation, string Status, string? ResultType, string QuotaUnit,
    int ReservedUnits, int? ConsumedUnits, AiRequestInputView Input, IReadOnlyList<AiPinnedSourceView> Sources, AiRequestResultView? Result,
    IReadOnlyList<AiCitationView> Citations, string? FailureCode, bool? ResultWithheld, DateTime CreatedAt, DateTime? CompletedAt, long Revision);
public sealed record AiRequestPage(IReadOnlyList<AiRequestView> Items, int Total, int Page, int PageSize);
public sealed record AiAllocationView(Guid GrantId, int ReservedUnits, int? ConsumedUnits, string Status);
/// <summary>Admin detail: the tenant view plus owner, policy, attempts, allocations and the withheld provider payload.</summary>
public sealed record AdminAiRequestView(Guid RequestId, string Audience, string Operation, string Status, string? ResultType, string QuotaUnit,
    int ReservedUnits, int? ConsumedUnits, AiRequestInputView Input, IReadOnlyList<AiPinnedSourceView> Sources, AiRequestResultView? Result,
    IReadOnlyList<AiCitationView> Citations, string? FailureCode, DateTime CreatedAt, DateTime? CompletedAt, long Revision,
    Guid? OrganizationId, Guid ActorUserId, Guid PolicyId, int Attempts, string? ModelVersion, JsonElement? TechnicalUsage,
    DateTime? ReconcileRequestedAt, JsonElement? ProviderResult, DateTime UpdatedAt, IReadOnlyList<AiAllocationView>? Allocations);
public sealed record AdminAiRequestPage(IReadOnlyList<AdminAiRequestView> Items, int Total, int Page, int PageSize);
/// <summary>Cancelled (end only): the policy had not started and was removed instead of ended.</summary>
public sealed record AiPolicyView(Guid Id, string Operation, string QuotaUnit, int ReserveUnits, int MaxInputChars, int MaxSources, int? DailyUnits,
    DateTime EffectiveFrom, DateTime? EffectiveUntil, DateTime CreatedAt, bool? Cancelled = null);
public sealed record AiPolicyPage(IReadOnlyList<AiPolicyView> Items, int Total, int Page, int PageSize);
/// <summary>Per operation: what one request reserves now and whether the organization balance covers it (estimatedReserve, #89).</summary>
public sealed record AiOperationLimitView(string Operation, bool PolicyActive, string? QuotaUnit, int? EstimatedReserve, int? MaxInputChars, int? MaxSources,
    long AvailableUnits, bool CanSubmit);
public sealed record AiLimitsView(IReadOnlyList<AiOperationLimitView> Items);
/// <summary>Today's Trainee allowance (Asia/Ho_Chi_Minh day): used + reserved never exceed dailyUnits; resetsAt is the next day start.</summary>
public sealed record TraineeAiQuotaView(bool PolicyActive, string? QuotaUnit, int? DailyUnits, long UsedUnits, long ReservedUnits, long RemainingUnits,
    int? EstimatedReserve, int? MaxInputChars, DateTime ResetsAt, bool BlockedByAssessment, bool CanSubmit);

/// <summary>
/// Organization AI through an external FastAPI adapter. Disabled by default; without a configured adapter submit returns
/// 503 AI_PROVIDER_UNAVAILABLE and nothing is reserved. Invalid settings, including values of the wrong type, never stop the API
/// (#176): AI stays off, the worker logs an error and /health/ready lists AI_CONFIGURATION_INVALID without failing.
/// </summary>
public sealed class OrganizationAiOptions
{
    public const string Section = "OrganizationAi";
    public bool Enabled { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public int LeaseSeconds { get; init; } = 180;
    public int MaxAttempts { get; init; } = 3;
    public int BackoffSeconds { get; init; } = 30;
    public int PollSeconds { get; init; } = 5;
    public bool BaseUrlValid => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
    public bool TimingValid => TimeoutSeconds is >= 5 and <= 600 && LeaseSeconds > TimeoutSeconds && LeaseSeconds <= 3600 && MaxAttempts is >= 1 and <= 10
        && BackoffSeconds is >= 1 and <= 3600 && PollSeconds is >= 1 and <= 300;
    /// <summary>A value of the section has the wrong type (e.g. TimeoutSeconds=abc); set by the binder wrapper, never bound from configuration.</summary>
    public bool Unreadable { get; private set; }
    public void MarkUnreadable() => Unreadable = true;
    public bool Configured => !Unreadable && Enabled && BaseUrlValid && TimingValid;
    /// <summary>Why AI is off although configured, without values (the URL may carry secrets); null when nothing is wrong.</summary>
    public string? Problem => Unreadable ? "OrganizationAi settings could not be read: a value has the wrong type."
        : !Enabled || Configured ? null : !BaseUrlValid ? "OrganizationAi:BaseUrl must be an absolute HTTPS (or loopback) URL when enabled." : "OrganizationAi timing settings are out of range.";
}

public interface IOrganizationAiGates
{
    Task<AuthResult<JsonElement>> OrganizationAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, CancellationToken ct);
    /// <summary>The caller's organization AI requests, newest first (status, operation, mine, page, pageSize).</summary>
    Task<AuthResult<JsonElement>> OrganizationRequestsAsync(Guid actor, Guid family, object input, CancellationToken ct);
    /// <summary>Trainee entry points: Quota, Submit, Get, List.</summary>
    Task<AuthResult<JsonElement>> TraineeAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, CancellationToken ct);
    Task<AuthResult<JsonElement>> AdminAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct);
    Task<JsonElement> WorkerAsync(string action, string worker, Guid? request, Guid? lease, object input, CancellationToken ct);
}

/// <summary>Outcome of one adapter call. Delivered=false only when the request provably never reached the service.</summary>
public enum AiCallStatus { Completed, NotDelivered, Unknown, Rejected, NotFound }
public sealed record AiCallResult(AiCallStatus Status, JsonObject? Body = null, string? Reason = null);

public interface IAiProviderClient
{
    Task<AiCallResult> DispatchAsync(JsonElement claim, CancellationToken ct);
    Task<AiCallResult> LookupAsync(Guid requestId, CancellationToken ct);
}

public static partial class OrganizationAiInput
{
    public static readonly string[] SourceKinds = ["KnowledgeSource", "LibraryVersion", "LearnPost"];
    public static readonly string[] TraineeSourceKinds = ["KnowledgeSource", "LearnPost"];
    public static readonly string[] ResultStatuses = ["Answered", "NeedsUserEdit", "InsufficientEvidence", "RejectedBySafetyGate"];
    public const int MaxTextLength = 8000;
    [GeneratedRegex(@"<\s*[a-zA-Z/!?]")] private static partial Regex Markup();
    [GeneratedRegex("^[0-9a-f]{64}$")] public static partial Regex Sha256();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")] private static partial Regex VersionLabel();
    /// <summary>Units the AI service settles usage in (AI_QUOTA_UNIT_UNSUPPORTED otherwise); AI policies and AI quota grants use only these.
    /// AiPolicyEndAndUnits.sql repeats the list; a test keeps the two equal.</summary>
    public static readonly FrozenSet<string> QuotaUnits = FrozenSet.ToFrozenSet(["tokens", "requests"], StringComparer.Ordinal);
    public static bool SupportedQuotaUnit(string? unit) => unit is not null && QuotaUnits.Contains(unit);

    public static List<ScenarioDraftValidationIssue> Text(string? value, string path, int max = MaxTextLength)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (string.IsNullOrWhiteSpace(value)) issues.Add(new("FIELD_REQUIRED", path, "Required."));
        else if (value.Length > max) issues.Add(new("VALUE_TOO_LONG", path, $"At most {max} characters."));
        else if (Markup().IsMatch(value) || value.Any(c => char.IsControl(c) && c is not '\n' and not '\t' and not '\r')) issues.Add(new("MARKUP_NOT_ALLOWED", path, "Plain text only."));
        return issues;
    }

    /// <summary>What a knowledge source version is registered with, whether by URL and hash or by uploading the file (BE#179).</summary>
    public static List<ScenarioDraftValidationIssue> SourceRegistration(string? visibility, string? title, string? versionLabel, Guid? organizationId,
        string? jurisdiction, DateTimeOffset? effectiveFrom, DateTimeOffset? effectiveUntil)
    {
        var issues = Text(title, "$.title", 500);
        if (visibility is not ("Common" or "Organization")) issues.Add(new("VALUE_UNSUPPORTED", "$.visibility", "Common or Organization."));
        if ((visibility == "Organization") != (organizationId is not null)) issues.Add(new("FIELD_INVALID", "$.organizationId", "Required for Organization sources only."));
        if (!VersionLabel().IsMatch(versionLabel ?? "")) issues.Add(new("VALUE_UNSUPPORTED", "$.versionLabel", "1-100 of letters, digits, dot, dash or underscore."));
        if (jurisdiction is not null) issues.AddRange(Text(jurisdiction, "$.jurisdiction", 100));
        if (effectiveFrom is not null && effectiveUntil <= effectiveFrom) issues.Add(new("VALUE_OUT_OF_RANGE", "$.effectiveUntil", "Must be after effectiveFrom."));
        return issues;
    }

    public static List<ScenarioDraftValidationIssue> Sources(IReadOnlyList<AiSourceRef>? sources, string[]? kinds = null)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (sources is null) return issues;
        kinds ??= SourceKinds;
        if (sources.Count > 100) issues.Add(new("VALUE_TOO_LONG", "$.sources", "At most 100 sources."));
        for (var i = 0; i < sources.Count; i++)
            if (!kinds.Contains(sources[i].Kind) || sources[i].Id == Guid.Empty) issues.Add(new("VALUE_UNSUPPORTED", $"$.sources[{i}]", $"Kind must be one of {string.Join(", ", kinds)} with a non-empty id."));
        if (sources.Select(x => (x.Kind, x.Id)).Distinct().Count() != sources.Count) issues.Add(new("DUPLICATE_SOURCE", "$.sources", "Sources must be distinct."));
        return issues;
    }

    /// <summary>
    /// Normalizes an adapter v2 response (Docs fire_evacuation_bim_rag_pccc.md section 6, snake_case) before the database
    /// settles it. Shape problems leave status null, which the gate parks as NeedsReconcile AI_RESULT_INVALID with the raw body
    /// kept for Admin; citations, BIM anchors and usage limits are re-checked there against the pinned request.
    /// </summary>
    public static (JsonObject Completion, string? Violation) Completion(string operation, Guid requestId, JsonObject body)
    {
        string? violation = null;
        static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var status = Str(body["status"]);
        var responseType = Str(body["response_type"]);
        var expectedType = operation == "ScenarioDraft" ? "ScenarioDraft" : "KnowledgeAnswer";
        var result = new JsonObject();
        if (Str(body["request_id"]) != requestId.ToString() || status is null || !ResultStatuses.Contains(status) || responseType != expectedType
            || (status == "NeedsUserEdit" && operation != "ScenarioDraft") || (status == "Answered" && operation == "ScenarioDraft"))
            violation = "AI_RESULT_INVALID";
        else if (status == "NeedsUserEdit")
        {
            if (body["scenario_draft"] is not JsonObject draft || draft["state"] is not JsonObject state || ScenarioStateV1Validator.ValidateShape(state).Count > 0) violation = "AI_RESULT_INVALID";
            else result["draft"] = new JsonObject { ["state"] = state.DeepClone(), ["notes"] = PlainOrNull(draft["notes"] ?? body["recommendation"]) };
        }
        else if (status == "Answered")
        {
            var answer = Str(body["recommendation"]);
            if (Text(answer, "$.recommendation").Count > 0) violation = "AI_RESULT_INVALID";
            else result["answer"] = answer;
        }
        else result["message"] = PlainOrNull(body["recommendation"]);
        if (body["confidence"] is not null)
        {
            if (Str(body["confidence"]) is "low" or "medium" or "high") result["confidence"] = Str(body["confidence"]);
            else violation ??= "AI_RESULT_INVALID";
        }
        result["assumptions"] = PlainList(body["assumptions"], ref violation);
        result["limitations"] = PlainList(body["limitations"], ref violation);
        var rules = new JsonArray();
        if (body["rule_results"] is JsonArray ruleList)
            foreach (var rule in ruleList)
            {
                if (rule is not JsonObject r || Str(r["rule_id"]) is not { Length: > 0 and <= 100 } id || Str(r["outcome"]) is not ("flag" or "pass" or "not_evaluable")) { violation ??= "AI_RESULT_INVALID"; continue; }
                rules.Add(new JsonObject { ["ruleId"] = id, ["outcome"] = Str(r["outcome"]), ["reason"] = PlainOrNull(r["reason"]) });
            }
        else if (body["rule_results"] is not null) violation ??= "AI_RESULT_INVALID";
        result["ruleResults"] = rules;

        var citations = new JsonArray();
        if (body["citations"] is JsonArray list)
            foreach (var c in list)
            {
                if (c is not JsonObject cite || !Guid.TryParse(Str(cite["document_id"]), out var document) || !Guid.TryParse(Str(cite["document_version"]), out var version)) { violation ??= "AI_RESULT_INVALID"; continue; }
                var entry = new JsonObject { ["documentId"] = document.ToString(), ["documentVersion"] = version.ToString() };
                if (Str(cite["chunk_id"]) is { } chunk && Text(chunk, "$", 200).Count == 0) entry["chunkId"] = chunk;
                if (Str(cite["excerpt_locator"]) is { } locator && Text(locator, "$", 500).Count == 0) entry["locator"] = locator;
                if (cite["relevance"] is JsonValue rel && rel.TryGetValue<double>(out var relevance) && relevance is >= 0 and <= 1) entry["relevance"] = relevance;
                citations.Add(entry);
            }
        else violation ??= "AI_RESULT_INVALID";
        var anchors = new JsonArray();
        if (body["bim_anchors"] is JsonArray anchorList)
            foreach (var a in anchorList)
            {
                if (a is not JsonObject anchor || Str(anchor["ifc_global_id"]) is not { Length: > 0 and <= 64 } ifc || !Guid.TryParse(Str(anchor["revision_id"]), out var revision)) { violation ??= "AI_RESULT_INVALID"; continue; }
                anchors.Add(new JsonObject { ["ifcGlobalId"] = ifc, ["entityType"] = PlainOrNull(anchor["entity_type"]), ["revisionId"] = revision.ToString() });
            }
        else if (body["bim_anchors"] is not null) violation ??= "AI_RESULT_INVALID";
        var usage = body["usage"] as JsonObject;
        JsonNode? Units(string name) => usage?[name] is JsonValue v && v.TryGetValue<long>(out var n) && n >= 0 ? n : null;
        return (new JsonObject
        {
            ["requestId"] = requestId.ToString(),
            ["status"] = violation is null ? status : null,
            ["responseType"] = responseType,
            ["result"] = violation is null ? result : null,
            ["citations"] = citations,
            ["bimAnchors"] = anchors,
            ["usage"] = new JsonObject { ["units"] = Units("provider_units"), ["inputTokens"] = Units("input_tokens"), ["outputTokens"] = Units("output_tokens") },
            ["modelVersion"] = PlainOrNull(usage?["model_version"]),
            ["raw"] = violation is null ? null : body.DeepClone()
        }, violation);
    }

    private static JsonArray PlainList(JsonNode? node, ref string? violation)
    {
        var list = new JsonArray();
        if (node is null) return list;
        if (node is not JsonArray items || items.Count > 20) { violation ??= "AI_RESULT_INVALID"; return list; }
        foreach (var item in items)
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && Text(s, "$", 500).Count == 0) list.Add(s);
            else violation ??= "AI_RESULT_INVALID";
        return list;
    }

    private static string? PlainOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && Text(s, "$", 2000).Count == 0 ? s : null;
}
