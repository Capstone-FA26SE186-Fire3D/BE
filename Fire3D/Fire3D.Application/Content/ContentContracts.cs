using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;

namespace Fire3D.Application.Content;

public sealed record LearnVersionInput(string Kind, string Title, string Summary, string? CoverImageUrl, JsonArray Blocks,
    IReadOnlyList<Guid>? SituationIds = null, IReadOnlyList<LearnSourceInput>? Sources = null);
public sealed record LearnSourceInput(Guid SourceId, string Role = "Reference", JsonObject? Locator = null);
public sealed record CreateLearnPostRequest(string Slug, LearnVersionInput Version, bool Publish = false);
public sealed record CreateLearnSituationRequest(string Slug, string Name, string? Description = null);
public sealed record MediaValidationRequest(string Url);
public sealed record MediaDescriptor(string Provider, string VideoId, string CanonicalUrl);
public sealed record CreateLibraryItemRequest(string Kind, string Code);
public sealed record UpdateLibraryItemRequest(bool IsActive);
public sealed record LibraryVersionInput(string Name, JsonObject Payload, IReadOnlyList<string>? RequiredCapabilities = null);

/// <summary>Gate result passthrough: JSON representations are returned as documented in docs/learn-library.md.</summary>
public interface IContentGates
{
    Task<AuthResult<JsonElement>> LearnPublicAsync(string action, Guid? actor, Guid? family, object input, CancellationToken ct);
    Task<AuthResult<JsonElement>> LearnAdminAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct);
    Task<AuthResult<JsonElement>> LibraryAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct);
}

/// <summary>
/// Learn content fet3d.learn/1: plain-text blocks (heading, paragraph, list, callout, image, video). No HTML, iframe or script;
/// videos are reduced to an allowlisted provider descriptor and never fetched or indexed by the backend.
/// </summary>
public static partial class LearnContent
{
    public const string SchemaVersion = "fet3d.learn/1";
    public static readonly string[] Kinds = ["Article", "Tip", "Video"];
    [GeneratedRegex(@"<\s*[a-zA-Z/!?]")] private static partial Regex Markup();
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")] private static partial Regex YouTubeId();
    [GeneratedRegex(@"^[0-9]{8,25}$")] private static partial Regex NumericId();
    [GeneratedRegex(@"^@[A-Za-z0-9._]{2,24}$")] private static partial Regex TikTokHandle();

    /// <summary>Canonical descriptor for an allowlisted video URL, or null.</summary>
    public static MediaDescriptor? Canonicalize(string? url)
    {
        if (url is null || url.Length > 2048 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            return null;
        var host = uri.Host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m." }) if (host.StartsWith(prefix)) host = host[prefix.Length..];
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        switch (host)
        {
            case "youtube.com":
            {
                var id = segments switch { ["watch"] => query["v"], ["shorts" or "embed" or "live", var v] => v, _ => null };
                return id is not null && YouTubeId().IsMatch(id) ? new("YouTube", id, $"https://www.youtube.com/watch?v={id}") : null;
            }
            case "youtu.be":
                return segments is [var short_] && YouTubeId().IsMatch(short_) ? new("YouTube", short_, $"https://www.youtube.com/watch?v={short_}") : null;
            case "tiktok.com":
                return segments is [var handle, "video", var tid] && TikTokHandle().IsMatch(handle) && NumericId().IsMatch(tid) ? new("TikTok", tid, $"https://www.tiktok.com/{handle}/video/{tid}") : null;
            case "facebook.com":
            {
                var id = segments switch { ["watch"] => query["v"], [_, "videos", var v, ..] => v, ["reel", var v] => v, _ => null };
                return id is not null && NumericId().IsMatch(id) ? new("Facebook", id, $"https://www.facebook.com/watch/?v={id}") : null;
            }
            default:
                return null; // Includes fb.watch and other short links that would need a network fetch to resolve.
        }
    }

    /// <summary>Validates and canonicalises a version; returns issues or the canonical content object stored and hashed by the gate.</summary>
    public static (IReadOnlyList<ScenarioDraftValidationIssue> Issues, JsonObject? Content) Normalize(LearnVersionInput input)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        void Add(string code, string path, string message) => issues.Add(new(code, path, message));
        string? Text(string? value, string path, int max, bool required = true)
        {
            if (value is null) { if (required) Add("FIELD_REQUIRED", path, "Text is required."); return null; }
            var t = value.Trim();
            if (t.Length == 0 || t.Length > max) { Add("STRING_INVALID", path, $"Use 1-{max} characters."); return null; }
            if (Markup().IsMatch(t)) { Add("MARKUP_NOT_ALLOWED", path, "HTML, iframe and script are not accepted; use plain text."); return null; }
            if (t.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) { Add("STRING_INVALID", path, "Control characters are not allowed."); return null; }
            return t;
        }
        string? Https(string? value, string path, bool required)
        {
            if (value is null) { if (required) Add("FIELD_REQUIRED", path, "An https URL is required."); return null; }
            return Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && value.Length <= 2048 && string.IsNullOrEmpty(u.UserInfo) ? u.AbsoluteUri : Fail();
            string? Fail() { Add("URL_INVALID", path, "Use an absolute https URL without credentials."); return null; }
        }
        if (!Kinds.Contains(input.Kind)) Add("VALUE_UNSUPPORTED", "$.kind", "Use Article, Tip or Video.");
        var title = Text(input.Title, "$.title", 255); var summary = Text(input.Summary, "$.summary", 2000);
        var cover = Https(input.CoverImageUrl, "$.coverImageUrl", false);
        var blocks = new JsonArray(); var videos = 0;
        if (input.Blocks is null || input.Blocks.Count is 0 or > 200) Add("FIELD_REQUIRED", "$.blocks", "Use 1-200 blocks.");
        else for (var i = 0; i < input.Blocks.Count; i++)
        {
            var path = $"$.blocks[{i}]";
            if (input.Blocks[i] is not JsonObject b || b["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var type)) { Add("TYPE_INVALID", path, "Each block is an object with a type."); continue; }
            string[] allowed = type switch
            {
                "heading" => ["type", "text", "level"], "paragraph" => ["type", "text"], "list" => ["type", "ordered", "items"],
                "callout" => ["type", "tone", "text"], "image" => ["type", "url", "alt"], "video" => ["type", "url"], _ => []
            };
            if (allowed.Length == 0) { Add("VALUE_UNSUPPORTED", $"{path}.type", "Use heading, paragraph, list, callout, image or video."); continue; }
            foreach (var (name, _) in b) if (!allowed.Contains(name)) Add("FIELD_UNKNOWN", $"{path}.{name}", "Field is not defined for this block type.");
            string? S(string name, int max) => b[name] is JsonValue v && v.TryGetValue<string>(out var s) ? Text(s, $"{path}.{name}", max) : Text(null, $"{path}.{name}", max);
            switch (type)
            {
                case "heading":
                    var level = b["level"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;
                    if (level is < 2 or > 4) Add("VALUE_UNSUPPORTED", $"{path}.level", "Heading level is 2, 3 or 4.");
                    if (S("text", 200) is { } h) blocks.Add(new JsonObject { ["type"] = type, ["text"] = h, ["level"] = level });
                    break;
                case "paragraph":
                    if (S("text", 5000) is { } p) blocks.Add(new JsonObject { ["type"] = type, ["text"] = p });
                    break;
                case "callout":
                    var tone = b["tone"] is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : null;
                    if (tone is not ("info" or "warning" or "danger")) Add("VALUE_UNSUPPORTED", $"{path}.tone", "Tone is info, warning or danger.");
                    if (S("text", 2000) is { } ct && tone is not null) blocks.Add(new JsonObject { ["type"] = type, ["tone"] = tone, ["text"] = ct });
                    break;
                case "list":
                    var ordered = b["ordered"] is JsonValue ov && ov.TryGetValue<bool>(out var o) && o;
                    var items = new JsonArray();
                    if (b["items"] is not JsonArray list || list.Count is 0 or > 50) Add("FIELD_REQUIRED", $"{path}.items", "Use 1-50 items.");
                    else for (var j = 0; j < list.Count; j++)
                        if ((list[j] is JsonValue iv && iv.TryGetValue<string>(out var item) ? Text(item, $"{path}.items[{j}]", 1000) : Text(null, $"{path}.items[{j}]", 1000)) is { } t) items.Add(t);
                    blocks.Add(new JsonObject { ["type"] = type, ["ordered"] = ordered, ["items"] = items });
                    break;
                case "image":
                    var url = b["url"] is JsonValue uv && uv.TryGetValue<string>(out var u) ? Https(u, $"{path}.url", true) : Https(null, $"{path}.url", true);
                    if (url is not null && S("alt", 300) is { } alt) blocks.Add(new JsonObject { ["type"] = type, ["url"] = url, ["alt"] = alt });
                    break;
                case "video":
                    var media = b["url"] is JsonValue vv && vv.TryGetValue<string>(out var vu) ? Canonicalize(vu) : null;
                    if (media is null) Add("LEARN_MEDIA_INVALID", $"{path}.url", "Use a YouTube, Facebook or TikTok video URL in its full form.");
                    else { videos++; blocks.Add(new JsonObject { ["type"] = type, ["provider"] = media.Provider, ["videoId"] = media.VideoId, ["url"] = media.CanonicalUrl }); }
                    break;
            }
        }
        if (input.Kind == "Video" && videos == 0) Add("VIDEO_REQUIRED", "$.blocks", "A Video post needs at least one video block.");
        var situations = input.SituationIds ?? [];
        if (situations.Count > 20 || situations.Distinct().Count() != situations.Count || situations.Contains(Guid.Empty)) Add("SITUATIONS_INVALID", "$.situationIds", "Use up to 20 distinct situations.");
        var sources = input.Sources ?? [];
        if (sources.Count > 20 || sources.Any(s => s.SourceId == Guid.Empty || s.Role is not ("Primary" or "Reference" or "Transcript")) || sources.Select(s => (s.SourceId, s.Role)).Distinct().Count() != sources.Count)
            Add("SOURCES_INVALID", "$.sources", "Use up to 20 distinct sources with role Primary, Reference or Transcript.");
        if (issues.Count > 0) return (issues, null);
        var content = new JsonObject
        {
            ["contentSchemaVersion"] = SchemaVersion, ["kind"] = input.Kind, ["title"] = title, ["summary"] = summary, ["coverImageUrl"] = cover, ["blocks"] = blocks,
            ["situationIds"] = new JsonArray(situations.Order().Select(x => (JsonNode)x.ToString()).ToArray()),
            ["sources"] = new JsonArray(sources.Select(s => (JsonNode)new JsonObject { ["sourceId"] = s.SourceId.ToString(), ["role"] = s.Role, ["locator"] = s.Locator?.DeepClone() ?? new JsonObject() }).ToArray())
        };
        if (content.ToJsonString().Length > 262_144) return ([new("CONTENT_TOO_LARGE", "$", "Content must be at most 256 KB.")], null);
        return (issues, content);
    }
}

/// <summary>Library payloads per kind. Equipment metadata never enables a runtime capability.</summary>
public static class LibraryContent
{
    public static IReadOnlyList<ScenarioDraftValidationIssue> Validate(string kind, LibraryVersionInput input)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 255) issues.Add(new("STRING_INVALID", "$.name", "Name is 1-255 characters."));
        if (input.Payload is null) { issues.Add(new("FIELD_REQUIRED", "$.payload", "Payload is an object.")); return issues; }
        if (input.Payload.ToJsonString().Length > 262_144) issues.Add(new("CONTENT_TOO_LARGE", "$.payload", "Payload must be at most 256 KB."));
        var caps = input.RequiredCapabilities ?? [];
        if (caps.Count > 50 || caps.Distinct().Count() != caps.Count || caps.Any(c => c is null || !Regex.IsMatch(c, "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$")))
            issues.Add(new("CAPABILITIES_INVALID", "$.requiredCapabilities", "Use up to 50 distinct capability IDs."));
        string[] allowed = kind switch { "ScenarioTemplate" => ["state", "description"], "RubricSample" => ["rubric", "description"], _ => ["description", "properties"] };
        foreach (var (name, _) in input.Payload) if (!allowed.Contains(name)) issues.Add(new("FIELD_UNKNOWN", $"$.payload.{name}", "Field is not defined for this library kind."));
        if (input.Payload["description"] is { } d && (d is not JsonValue dv || !dv.TryGetValue<string>(out var text) || text.Length is 0 or > 2000))
            issues.Add(new("STRING_INVALID", "$.payload.description", "Description is 1-2000 characters."));
        switch (kind)
        {
            case "ScenarioTemplate":
                var state = input.Payload["state"];
                if (EditorContract.DeclaredVersion(state) != EditorContract.V1) issues.Add(new("EDITOR_SCHEMA_VERSION_UNSUPPORTED", "$.payload.state", "Templates use the fet3d.editor/1 draft state."));
                else issues.AddRange(ScenarioStateV1Validator.ValidateShape(state).Select(x => x with { Path = "$.payload.state" + x.Path[1..] }));
                break;
            case "RubricSample":
                issues.AddRange(ScenarioStateV1Validator.ValidateRubricDocument(input.Payload["rubric"]).Select(x => x with { Path = "$.payload" + x.Path[1..] }));
                break;
            default:
                if (input.Payload["properties"] is { } props && (props is not JsonObject po || po.Count > 50 || po.Any(p => p.Value is not JsonValue pv || pv.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))))
                    issues.Add(new("PROPERTIES_INVALID", "$.payload.properties", "Properties are up to 50 string, number or boolean values."));
                break;
        }
        return issues;
    }
}
