using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
// Response shapes of the Learn gates (fet3d.learn/1), declared so OpenAPI describes what the SQL representations return (#83).
public sealed record LearnSituationView(Guid Id, string Slug, string Name, string? Description);
/// <summary>Admin catalog row and the PATCH result; IsActive=false keeps stored links but leaves public filters.</summary>
public sealed record LearnSituationAdminView(Guid Id, string Slug, string Name, string? Description, bool IsActive, DateTime? CreatedAt = null, DateTime? UpdatedAt = null);
public sealed record LearnSituationPage(IReadOnlyList<LearnSituationAdminView> Items, int Total, int Page, int PageSize);
public sealed record LearnSituationRef(Guid Id, string Slug, string Name);
public sealed record LearnSourceRef(Guid SourceId, string Title, string VersionLabel, string Role, JsonElement Locator);
/// <summary>Blocks are the normalized content: heading/paragraph/list/callout/image/video/divider. Text blocks always carry plain text and
/// add runs when formatted (lists add itemRuns); video carries provider, videoId and canonical url.</summary>
public sealed record LearnVersionView(Guid Id, Guid PostId, int VersionNumber, string ContentSchemaVersion, string Kind, string Title, string Summary,
    string? CoverImageUrl, JsonElement Blocks, string ContentHash, string Status, DateTime? PublishedAt, long Revision, DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<LearnSituationRef> Situations, IReadOnlyList<LearnSourceRef> Sources);
public sealed record LearnVersionSummary(Guid Id, int VersionNumber, string Status, string Title, long Revision, DateTime UpdatedAt);
/// <summary>PublicationStatus: Unpublished, Published, Hidden or Deleted. Public reads omit Versions; admin reads list every version.</summary>
public sealed record LearnPostView(Guid Id, string Slug, string PublicationStatus, Guid? PublishedVersionId, long Revision, DateTime CreatedAt, DateTime UpdatedAt,
    LearnVersionView? PublishedVersion, IReadOnlyList<LearnVersionSummary>? Versions = null);
/// <summary>Admin list row. Title, Kind and the LatestVersion* fields describe the newest version of any status (Draft or Published), so every post,
/// including Unpublished and Deleted ones, has a title; the public title is PublishedVersion.Title. DraftVersionId is the newest open Draft or null.</summary>
public sealed record LearnPostAdminRow(Guid Id, string Slug, string PublicationStatus, Guid? PublishedVersionId, long Revision, DateTime CreatedAt, DateTime UpdatedAt,
    LearnVersionView? PublishedVersion, string? Title, string? Kind, Guid? LatestVersionId, int? LatestVersionNumber, string? LatestVersionStatus, Guid? DraftVersionId,
    int? DraftVersionNumber, int VersionCount);
public sealed record LearnPostAdminPage(IReadOnlyList<LearnPostAdminRow> Items, int Total, int Page, int PageSize);
public sealed record LearnPostSummary(Guid Id, string Slug, string Kind, string Title, string Summary, string? CoverImageUrl, DateTime PublishedAt);
public sealed record LearnPostPage(IReadOnlyList<LearnPostSummary> Items, int Total, int Page, int PageSize);
/// <summary>Available=false when the post is no longer public: only PostId, Available and BookmarkedAt are returned then.</summary>
public sealed record LearnBookmarkView(Guid PostId, bool Available, DateTime BookmarkedAt, string? Slug = null, string? Title = null, string? Summary = null, string? Kind = null);
/// <summary>Every bookmark of the Trainee whatever the filters and page: available ones by kind (keys are the kind values), the rest as unavailable.</summary>
public sealed record LearnBookmarkCounts([property: JsonPropertyName("Article")] int Article, [property: JsonPropertyName("Tip")] int Tip,
    [property: JsonPropertyName("Video")] int Video, int Unavailable);
/// <summary>Total counts the bookmarks matching kind and search; Counts ignores them.</summary>
public sealed record LearnBookmarkPage(IReadOnlyList<LearnBookmarkView> Items, int Total, int Page, int PageSize, LearnBookmarkCounts Counts);
public sealed record LearnBookmarkState(Guid PostId, bool Bookmarked);
/// <summary>Knowledge source registry row. CanUseInDraft: a Learn draft may cite it; CanPublishReference: publish, Hidden and RAG accept it now.</summary>
public sealed record KnowledgeSourceView(Guid Id, string Visibility, Guid? OrganizationId, string Title, string? SourceUri, string VersionLabel, string SourceHash,
    string? Jurisdiction, DateTime? EffectiveFrom, DateTime? EffectiveUntil, string ApprovalStatus, DateTime? ApprovedAt, DateTime CreatedAt, long Revision,
    bool CanUseInDraft, bool CanPublishReference);
public sealed record KnowledgeSourcePage(IReadOnlyList<KnowledgeSourceView> Items, int Total, int Page, int PageSize);
/// <summary>Send at least one field. Name 1-255 characters; an empty Description clears it; IsActive=false hides the situation from public filters and new publishes. The slug never changes.</summary>
public sealed record UpdateLearnSituationRequest(string? Name = null, string? Description = null, bool? IsActive = null);
public sealed record MediaValidationRequest(string Url);
public sealed record MediaDescriptor(string Provider, string VideoId, string CanonicalUrl);
public sealed record CreateLibraryItemRequest(string Kind, string Code);
/// <summary>Only isActive changes: code is the stable business key and kind fixes the payload schema, so both are immutable.</summary>
public sealed record UpdateLibraryItemRequest(bool IsActive);
/// <summary>Payload fields by kind: ScenarioTemplate {state (fet3d.editor/1), description?}, RubricSample {rubric, description?},
/// Equipment {description?, properties?}, StylePreset {buildingTypes, keywords?, materials, defaultScheme?, description?},
/// ColorScheme {colors{wall, accentWall, doorWood, skirting}, description?} (STYLE-01, docs/revision-style.md).
/// description is optional for every kind (1-2000 characters when present).
/// RequiredCapabilities must be offered by an active runtime catalog row when the version is published.</summary>
public sealed record LibraryVersionInput(string Name, JsonObject Payload, IReadOnlyList<string>? RequiredCapabilities = null);
public sealed record LibraryVersionResponse(Guid Id, Guid ItemId, string Kind, string Code, int VersionNumber, string Name, JsonElement Payload,
    IReadOnlyList<string> RequiredCapabilities, string PayloadHash, string Status, DateTime? PublishedAt, long Revision, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record LibraryVersionSummary(Guid Id, int VersionNumber, string Name, string Status, long Revision, DateTime? PublishedAt);
/// <summary>Versions lists every version for admin reads and only Published ones for Organization reads, newest first.</summary>
public sealed record LibraryItemResponse(Guid Id, string Kind, string Code, bool IsActive, long Revision, DateTime CreatedAt, DateTime UpdatedAt,
    LibraryVersionResponse? LatestPublished, IReadOnlyList<LibraryVersionSummary> Versions);
public sealed record LibraryItemPage(IReadOnlyList<LibraryItemResponse> Items, int Total, int Page, int PageSize);

/// <summary>Gate result passthrough: JSON representations are returned as documented in docs/learn-library.md.</summary>
public interface IContentGates
{
    Task<AuthResult<JsonElement>> LearnPublicAsync(string action, Guid? actor, Guid? family, object input, CancellationToken ct);
    Task<AuthResult<JsonElement>> LearnAdminAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct);
    Task<AuthResult<JsonElement>> LibraryAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct);
    /// <summary>PlatformAdmin editor catalogs: KnowledgeSources (visibility, status, search) and Situations (search, isActive), paged.</summary>
    Task<AuthResult<JsonElement>> CatalogAsync(string action, Guid actor, Guid family, object input, CancellationToken ct);
    /// <summary>If-Match mutations of Learn ("learn") or Library ("library") with an optional Idempotency-Key that replays the first success.</summary>
    Task<AuthResult<JsonElement>> LifecycleAsync(string gate, string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct);
}

/// <summary>
/// Learn content fet3d.learn/1: blocks heading, paragraph, list, callout, image, video and divider. Text is plain or a list of
/// formatted runs (allowlisted marks, theme colour tokens, https links); never HTML, iframe or script. Videos are reduced to an
/// allowlisted provider descriptor and never fetched or indexed by the backend.
/// </summary>
public static partial class LearnContent
{
    public const string SchemaVersion = "fet3d.learn/1";
    public static readonly string[] Kinds = ["Article", "Tip", "Video"];
    public static readonly string[] Marks = ["bold", "italic", "underline", "strike"];
    /// <summary>Run colour and highlight are theme tokens, not colour codes, so text stays readable on light and dark backgrounds.</summary>
    public static readonly string[] ColorTokens = ["default", "ember", "danger", "warning", "success", "info"];
    public const int MaxRuns = 200;
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
        (string Text, JsonArray? Runs)? Plain(JsonNode? node, string path, int max) =>
            (node is JsonValue v && v.TryGetValue<string>(out var s) ? Text(s, path, max) : Text(null, path, max)) is { } t ? (t, null) : null;
        // A formatted text is a list of runs. The stored block always keeps the joined plain text, which search, RAG, summaries and
        // older readers use, and adds runs only when one is formatted; adjacent runs with equal formatting merge so equal content
        // hashes equally (text is the same as one unformatted run).
        (string Text, JsonArray? Runs)? Runs(JsonNode? node, string path, int max)
        {
            if (node is not JsonArray runs || runs.Count is 0 or > MaxRuns) { Add("RUNS_INVALID", path, $"Use 1-{MaxRuns} runs."); return null; }
            var merged = new List<(string Text, string[] Marks, string? Color, string? Highlight, string? Link)>();
            var ok = true;
            for (var k = 0; k < runs.Count; k++)
            {
                var rp = $"{path}[{k}]";
                if (runs[k] is not JsonObject run) { Add("TYPE_INVALID", rp, "Each run is an object with text."); ok = false; continue; }
                foreach (var (name, _) in run)
                    if (name is not ("text" or "marks" or "color" or "highlight" or "link")) { Add("FIELD_UNKNOWN", $"{rp}.{name}", "Field is not defined for a run."); ok = false; }
                // Runs keep their spaces; only the joined text is trimmed.
                var text = run["text"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : null;
                if (text is null || text.Length == 0 || text.Length > max) { Add("STRING_INVALID", $"{rp}.text", $"Use 1-{max} characters."); ok = false; text = null; }
                else if (Markup().IsMatch(text)) { Add("MARKUP_NOT_ALLOWED", $"{rp}.text", "HTML, iframe and script are not accepted; use plain text."); ok = false; }
                else if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) { Add("STRING_INVALID", $"{rp}.text", "Control characters are not allowed."); ok = false; }
                var marks = new List<string>();
                if (run["marks"] is { } markNode)
                {
                    if (markNode is not JsonArray markList) { Add("MARK_UNSUPPORTED", $"{rp}.marks", "Marks is an array of bold, italic, underline or strike."); ok = false; }
                    else for (var m = 0; m < markList.Count; m++)
                        if (markList[m] is JsonValue mv && mv.TryGetValue<string>(out var mark) && Marks.Contains(mark)) { if (!marks.Contains(mark)) marks.Add(mark); }
                        else { Add("MARK_UNSUPPORTED", $"{rp}.marks[{m}]", "Use bold, italic, underline or strike."); ok = false; }
                }
                string? Token(string name)
                {
                    if (run[name] is null) return null;
                    if (run[name] is JsonValue cv && cv.TryGetValue<string>(out var token) && ColorTokens.Contains(token)) return token == "default" ? null : token;
                    Add("COLOR_UNSUPPORTED", $"{rp}.{name}", "Use default, ember, danger, warning, success or info."); ok = false; return null;
                }
                var color = Token("color"); var highlight = Token("highlight");
                string? link = null;
                if (run["link"] is { } linkNode && (link = linkNode is JsonValue lv && lv.TryGetValue<string>(out var ls) ? Https(ls, $"{rp}.link", true) : Https(null, $"{rp}.link", true)) is null) ok = false;
                if (!ok || text is null) continue;
                var ordered = Marks.Where(marks.Contains).ToArray();
                if (merged.Count > 0 && merged[^1] is var last && last.Marks.SequenceEqual(ordered) && last.Color == color && last.Highlight == highlight && last.Link == link)
                    merged[^1] = last with { Text = last.Text + text };
                else merged.Add((text, ordered, color, highlight, link));
            }
            if (!ok) return null;
            var joined = string.Concat(merged.Select(r => r.Text)).Trim();
            if (joined.Length == 0 || joined.Length > max) { Add("STRING_INVALID", path, $"Use 1-{max} characters in total."); return null; }
            if (merged.All(r => r.Marks.Length == 0 && r.Color is null && r.Highlight is null && r.Link is null)) return (joined, null);
            var stored = new JsonArray();
            foreach (var r in merged)
            {
                var o = new JsonObject { ["text"] = r.Text };
                if (r.Marks.Length > 0) o["marks"] = new JsonArray(r.Marks.Select(x => (JsonNode)x).ToArray());
                if (r.Color is not null) o["color"] = r.Color;
                if (r.Highlight is not null) o["highlight"] = r.Highlight;
                if (r.Link is not null) o["link"] = r.Link;
                stored.Add(o);
            }
            return (joined, stored);
        }
        (string Text, JsonArray? Runs)? Body(JsonObject b, string path, int max)
        {
            if (b.ContainsKey("text") && b.ContainsKey("runs")) { Add("TEXT_OR_RUNS", path, "Send text or runs, not both."); return null; }
            return b.ContainsKey("runs") ? Runs(b["runs"], $"{path}.runs", max) : Plain(b["text"], $"{path}.text", max);
        }
        JsonObject Rich(JsonObject block, (string Text, JsonArray? Runs) body)
        {
            block["text"] = body.Text;
            if (body.Runs is not null) block["runs"] = body.Runs;
            return block;
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
                "heading" => ["type", "text", "runs", "level"], "paragraph" => ["type", "text", "runs"], "list" => ["type", "ordered", "items"],
                "callout" => ["type", "tone", "text", "runs"], "image" => ["type", "url", "alt"], "video" => ["type", "url"], "divider" => ["type"], _ => []
            };
            if (allowed.Length == 0) { Add("VALUE_UNSUPPORTED", $"{path}.type", "Use heading, paragraph, list, callout, image, video or divider."); continue; }
            foreach (var (name, _) in b) if (!allowed.Contains(name)) Add("FIELD_UNKNOWN", $"{path}.{name}", "Field is not defined for this block type.");
            string? S(string name, int max) => b[name] is JsonValue v && v.TryGetValue<string>(out var s) ? Text(s, $"{path}.{name}", max) : Text(null, $"{path}.{name}", max);
            switch (type)
            {
                case "heading":
                    var level = b["level"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : 0;
                    if (level is < 2 or > 4) Add("VALUE_UNSUPPORTED", $"{path}.level", "Heading level is 2, 3 or 4.");
                    if (Body(b, path, 200) is { } h) { var heading = Rich(new JsonObject { ["type"] = type }, h); heading["level"] = level; blocks.Add(heading); }
                    break;
                case "paragraph":
                    if (Body(b, path, 5000) is { } p) blocks.Add(Rich(new JsonObject { ["type"] = type }, p));
                    break;
                case "callout":
                    var tone = b["tone"] is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : null;
                    if (tone is not ("info" or "warning" or "danger")) Add("VALUE_UNSUPPORTED", $"{path}.tone", "Tone is info, warning or danger.");
                    if (Body(b, path, 2000) is { } ct && tone is not null) blocks.Add(Rich(new JsonObject { ["type"] = type, ["tone"] = tone }, ct));
                    break;
                case "list":
                    var ordered = b["ordered"] is JsonValue ov && ov.TryGetValue<bool>(out var o) && o;
                    // Items stay plain strings; when one item is formatted, itemRuns holds the runs of every item in the same order.
                    var items = new JsonArray(); var itemRuns = new JsonArray(); var richItems = false;
                    if (b["items"] is not JsonArray list || list.Count is 0 or > 50) Add("FIELD_REQUIRED", $"{path}.items", "Use 1-50 items.");
                    else for (var j = 0; j < list.Count; j++)
                    {
                        var ip = $"{path}.items[{j}]";
                        (string Text, JsonArray? Runs)? entry;
                        if (list[j] is JsonObject itemObject)
                        {
                            foreach (var (name, _) in itemObject) if (name != "runs") Add("FIELD_UNKNOWN", $"{ip}.{name}", "A list item is a string or an object with runs.");
                            entry = Runs(itemObject["runs"], $"{ip}.runs", 1000);
                        }
                        else entry = Plain(list[j], ip, 1000);
                        if (entry is not { } e) continue;
                        items.Add(e.Text);
                        richItems |= e.Runs is not null;
                        itemRuns.Add(e.Runs ?? new JsonArray(new JsonObject { ["text"] = e.Text }));
                    }
                    var listBlock = new JsonObject { ["type"] = type, ["ordered"] = ordered, ["items"] = items };
                    if (richItems) listBlock["itemRuns"] = itemRuns;
                    blocks.Add(listBlock);
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
                case "divider":
                    blocks.Add(new JsonObject { ["type"] = type });
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
    /// <summary>rubricMetrics: metrics a RubricSample may use (the active runtimes' union); null keeps the runtime 2.0.x list.</summary>
    public static IReadOnlyList<ScenarioDraftValidationIssue> Validate(string kind, LibraryVersionInput input, IReadOnlyCollection<string>? rubricMetrics = null)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 255) issues.Add(new("STRING_INVALID", "$.name", "Name is 1-255 characters."));
        if (input.Payload is null) { issues.Add(new("FIELD_REQUIRED", "$.payload", "Payload is an object.")); return issues; }
        if (input.Payload.ToJsonString().Length > 262_144) issues.Add(new("CONTENT_TOO_LARGE", "$.payload", "Payload must be at most 256 KB."));
        var caps = input.RequiredCapabilities ?? [];
        if (caps.Count > 50 || caps.Distinct().Count() != caps.Count || caps.Any(c => c is null || !Regex.IsMatch(c, "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$")))
            issues.Add(new("CAPABILITIES_INVALID", "$.requiredCapabilities", "Use up to 50 distinct capability IDs."));
        string[] allowed = kind switch
        {
            "ScenarioTemplate" => ["state", "description"], "RubricSample" => ["rubric", "description"],
            "StylePreset" => ["buildingTypes", "keywords", "materials", "defaultScheme", "description"], "ColorScheme" => ["colors", "description"],
            _ => ["description", "properties"]
        };
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
                issues.AddRange(ScenarioStateV1Validator.ValidateRubricDocument(input.Payload["rubric"], rubricMetrics).Select(x => x with { Path = "$.payload" + x.Path[1..] }));
                break;
            case "StylePreset":
                Fire3D.Application.Ifc.StyleRules.ValidatePreset(input.Payload, issues);
                break;
            case "ColorScheme":
                Fire3D.Application.Ifc.StyleRules.ValidateScheme(input.Payload, issues);
                break;
            default:
                if (input.Payload["properties"] is { } props && (props is not JsonObject po || po.Count > 50 || po.Any(p => p.Value is not JsonValue pv || pv.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))))
                    issues.Add(new("PROPERTIES_INVALID", "$.payload.properties", "Properties are up to 50 string, number or boolean values."));
                break;
        }
        return issues;
    }
}
