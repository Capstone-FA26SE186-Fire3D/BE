using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Application.Authentication;

namespace Fire3D.API.Controllers;

/// <summary>Knowledge source JSON from the gates, read for what the AI service needs (a pin) and annotated with what it last said (BE#187).</summary>
internal static class KnowledgeSourceIndexView
{
    /// <summary>The pin of an approved source, or null when the row lacks what the AI service needs (id, title and a content hash).</summary>
    public static AiSourcePin? Pin(JsonElement source)
    {
        if (!source.TryGetProperty("id", out var id) || !id.TryGetGuid(out var sourceId)) return null;
        var title = Text(source, "title");
        var hash = Text(source, "sourceHash");
        return title is null || hash is null ? null : new AiSourcePin(sourceId, title, Text(source, "versionLabel"), hash, Text(source, "sourceUri"));
    }

    /// <summary>A catalog page with aiIndexStatus (and aiIndexError) on each approved source. Other sources are left as they are.</summary>
    public static AuthResult<JsonElement> Page(AuthResult<JsonElement> page, IAiSourceIndex index)
    {
        if (!page.IsSuccess || JsonNode.Parse(page.Value.GetRawText()) is not JsonObject root || root["items"] is not JsonArray items) return page;
        foreach (var item in items.OfType<JsonObject>()) Annotate(item, index);
        return AuthResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(root));
    }

    public static JsonElement Item(JsonElement item, IAiSourceIndex index)
    {
        if (JsonNode.Parse(item.GetRawText()) is not JsonObject node) return item;
        Annotate(node, index);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Annotate(JsonObject item, IAiSourceIndex index)
    {
        if (item["approvalStatus"]?.GetValue<string>() != "Approved" || item["id"] is not JsonValue id || !id.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var sourceId)) return;
        var info = index.Of(sourceId);
        item["aiIndexStatus"] = info.Status;
        if (info.Error is not null) item["aiIndexError"] = info.Error;
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
