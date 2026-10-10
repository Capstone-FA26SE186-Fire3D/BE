using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;

namespace Fire3D.Application.Ai;

public sealed record AiSourceRef(string Kind, Guid Id);
public sealed record ScenarioDraftAiRequest(Guid BuildingId, string Prompt, IReadOnlyList<AiSourceRef>? Sources = null);
public sealed record AnswerAiRequest(string Question, IReadOnlyList<AiSourceRef>? Sources = null);
public sealed record CreateAiPolicyRequest(string Operation, string QuotaUnit, int ReserveUnits, int MaxInputChars, int MaxSources, DateTimeOffset? EffectiveFrom = null, DateTimeOffset? EffectiveUntil = null);
public sealed record CreateKnowledgeSourceRequest(string Visibility, string Title, string VersionLabel, string SourceHash, Guid? OrganizationId = null,
    string? SourceUri = null, string? Jurisdiction = null, DateTimeOffset? EffectiveFrom = null, DateTimeOffset? EffectiveUntil = null);

/// <summary>
/// Organization AI through an external FastAPI adapter. Disabled by default; without a configured adapter submit returns
/// 503 AI_PROVIDER_UNAVAILABLE and nothing is reserved.
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
    public bool Configured => Enabled && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
}

public interface IOrganizationAiGates
{
    Task<AuthResult<JsonElement>> OrganizationAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, CancellationToken ct);
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
    public static readonly string[] Outcomes = ["Succeeded", "InsufficientEvidence", "SafetyRejected"];
    public const int MaxTextLength = 8000;
    [GeneratedRegex(@"<\s*[a-zA-Z/!?]")] private static partial Regex Markup();
    [GeneratedRegex("^[0-9a-f]{64}$")] public static partial Regex Sha256();
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,49}$")] public static partial Regex QuotaUnit();

    public static List<ScenarioDraftValidationIssue> Text(string? value, string path, int max = MaxTextLength)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (string.IsNullOrWhiteSpace(value)) issues.Add(new("FIELD_REQUIRED", path, "Required."));
        else if (value.Length > max) issues.Add(new("VALUE_TOO_LONG", path, $"At most {max} characters."));
        else if (Markup().IsMatch(value) || value.Any(c => char.IsControl(c) && c is not '\n' and not '\t' and not '\r')) issues.Add(new("MARKUP_NOT_ALLOWED", path, "Plain text only."));
        return issues;
    }

    public static List<ScenarioDraftValidationIssue> Sources(IReadOnlyList<AiSourceRef>? sources)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (sources is null) return issues;
        if (sources.Count > 100) issues.Add(new("VALUE_TOO_LONG", "$.sources", "At most 100 sources."));
        for (var i = 0; i < sources.Count; i++)
            if (!SourceKinds.Contains(sources[i].Kind) || sources[i].Id == Guid.Empty) issues.Add(new("VALUE_UNSUPPORTED", $"$.sources[{i}]", "Use a kind and id from GET /api/ai/organization/sources."));
        if (sources.Select(x => (x.Kind, x.Id)).Distinct().Count() != sources.Count) issues.Add(new("DUPLICATE_SOURCE", "$.sources", "Sources must be distinct."));
        return issues;
    }

    /// <summary>
    /// Validates the adapter response envelope before the database settles it. Shape problems become a contract violation
    /// (NeedsReconcile) in the gate; citations and usage limits are re-checked there against the pinned request.
    /// </summary>
    public static (JsonObject Completion, string? Violation) Completion(string operation, Guid requestId, JsonObject body)
    {
        string? violation = null;
        var outcome = body["outcome"] is JsonValue o && o.TryGetValue<string>(out var s) ? s : null;
        var result = new JsonObject();
        if (body["requestId"]?.ToString() != requestId.ToString() || outcome is null || !Outcomes.Contains(outcome)) violation = "AI_RESULT_INVALID";
        else if (outcome == "Succeeded" && operation == "ScenarioDraft")
        {
            if (body["draft"] is not JsonObject draft || draft["state"] is not JsonObject state || ScenarioStateV1Validator.ValidateShape(state).Count > 0) violation = "AI_RESULT_INVALID";
            else result["draft"] = new JsonObject { ["state"] = state.DeepClone(), ["notes"] = PlainOrNull(draft["notes"]) };
        }
        else if (outcome == "Succeeded")
        {
            var answer = body["answer"] is JsonValue a && a.TryGetValue<string>(out var t) ? t : null;
            if (Text(answer, "$.answer").Count > 0) violation = "AI_RESULT_INVALID";
            else result["answer"] = answer;
        }
        else result["message"] = PlainOrNull(body["message"]);
        var citations = new JsonArray();
        if (body["citations"] is JsonArray list)
            foreach (var c in list)
            {
                if (c is not JsonObject cite || cite["kind"] is not JsonValue k || !k.TryGetValue<string>(out var kind) || !Guid.TryParse(cite["id"]?.ToString(), out var id)) { violation ??= "AI_RESULT_INVALID"; continue; }
                var entry = new JsonObject { ["kind"] = kind, ["id"] = id.ToString() };
                if (cite["locator"] is JsonValue loc && loc.TryGetValue<string>(out var locator) && Text(locator, "$", 500).Count == 0) entry["locator"] = locator;
                citations.Add(entry);
            }
        else violation ??= "AI_RESULT_INVALID";
        var usage = body["usage"] as JsonObject;
        return (new JsonObject
        {
            ["requestId"] = requestId.ToString(),
            ["outcome"] = violation is null ? outcome : null,
            ["result"] = violation is null ? result : null,
            ["citations"] = citations,
            ["usage"] = usage?.DeepClone()
        }, violation);
    }

    private static string? PlainOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && Text(s, "$", 2000).Count == 0 ? s : null;
}
