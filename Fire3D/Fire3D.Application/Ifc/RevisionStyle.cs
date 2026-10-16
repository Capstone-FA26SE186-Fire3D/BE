using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;

namespace Fire3D.Application.Ifc;

// STYLE-01 (Docs ifc_ingestion_visual_interaction.md 5.5): material presets and colour schemes are Organization Library kinds
// (StylePreset, ColorScheme) kept by PlatformAdmin; an OrganizationUser picks them, plus material overrides, per revision.

/// <summary>Replaces the material of one surface in one room category, e.g. {room: "wc", surface: "wall", material: "wall.tile"}; room "default" means every room.</summary>
public sealed record StyleOverride(string Room, string Surface, string Material);
/// <summary>PresetVersionId and SchemeVersionId are published versions of active StylePreset/ColorScheme Library items. Without a scheme the worker uses the preset's defaultScheme.</summary>
public sealed record RevisionStyleRequest(Guid PresetVersionId, Guid? SchemeVersionId = null, IReadOnlyList<StyleOverride>? Overrides = null);
public sealed record StyleLibraryRef(Guid VersionId, Guid ItemId, string Code, string Name, int VersionNumber, JsonElement Payload);
public sealed record RevisionStyleSelection(StyleLibraryRef? Preset, StyleLibraryRef? Scheme, IReadOnlyList<StyleOverride> Overrides, Guid SelectedBy, DateTime SelectedAt);
/// <summary>The newest earlier revision's selection of the same Building; it is a suggestion and is not applied until saved.</summary>
public sealed record RevisionStylePrevious(Guid FromRevisionId, Guid? PresetVersionId, Guid? SchemeVersionId, IReadOnlyList<StyleOverride>? Overrides);
/// <summary>Version is the selection overlay version (ETag); 0 and Selection null before the first save. Every save is a new immutable version.</summary>
public sealed record RevisionStyleView(Guid RevisionId, long Version, RevisionStyleSelection? Selection, RevisionStylePrevious? PreviousSelection);

public interface IRevisionStyles
{
    Task<AuthResult<RevisionStyleView>> GetAsync(Guid actor, Guid family, Guid revision, CancellationToken ct);
    Task<AuthResult<RevisionStyleView>> SaveAsync(Guid actor, Guid family, Guid revision, long expected, RevisionStyleRequest request, CancellationToken ct);
}

/// <summary>Shapes of StylePreset/ColorScheme payloads and of a revision selection.</summary>
public static partial class StyleRules
{
    public static readonly string[] Surfaces = ["floor", "wall", "ceiling"];
    public static readonly string[] SchemeColors = ["wall", "accentWall", "doorWood", "skirting"];
    public const string DefaultRoom = "default";
    public const int MaxOverrides = 200, MaxRooms = 50;
    [GeneratedRegex("^[a-z][a-z0-9_]{0,49}\\z")] public static partial Regex Category();
    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z0-9_]+){0,3}\\z")] public static partial Regex MaterialKey();
    [GeneratedRegex("^#[0-9A-Fa-f]{6}\\z")] public static partial Regex HexColor();
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,79}\\z")] public static partial Regex LibraryCode();

    public static List<ScenarioDraftValidationIssue> Validate(RevisionStyleRequest? request)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (request is null) { issues.Add(new("FIELD_REQUIRED", "$", "A body is required.")); return issues; }
        if (request.PresetVersionId == Guid.Empty) issues.Add(new("FIELD_REQUIRED", "$.presetVersionId", "Choose a StylePreset version."));
        if (request.SchemeVersionId == Guid.Empty) issues.Add(new("FIELD_INVALID", "$.schemeVersionId", "Omit it or choose a ColorScheme version."));
        var overrides = request.Overrides ?? [];
        if (overrides.Count > MaxOverrides) { issues.Add(new("ARRAY_TOO_LARGE", "$.overrides", $"At most {MaxOverrides} overrides.")); return issues; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < overrides.Count; i++)
        {
            var o = overrides[i]; var path = $"$.overrides[{i}]";
            if (o is null) { issues.Add(new("FIELD_REQUIRED", path, "An override object is required.")); continue; }
            if (o.Room is null || !Category().IsMatch(o.Room)) issues.Add(new("FIELD_INVALID", path + ".room", "A room category (lowercase) or \"default\"."));
            if (o.Surface is null || !Surfaces.Contains(o.Surface)) issues.Add(new("FIELD_INVALID", path + ".surface", "Use floor, wall or ceiling."));
            if (o.Material is null || o.Material.Length > 64 || !MaterialKey().IsMatch(o.Material)) issues.Add(new("FIELD_INVALID", path + ".material", "A material library key such as floor.tile."));
            if (o.Room is not null && o.Surface is not null && !seen.Add(o.Room + "." + o.Surface)) issues.Add(new("ID_DUPLICATE", path, "One override per room and surface."));
        }
        return issues;
    }

    /// <summary>StylePreset payload: {description?, buildingTypes[1-10], keywords?[0-100], materials{default{floor,wall,ceiling}, room{surface?}...}, defaultScheme?}.</summary>
    public static void ValidatePreset(JsonObject payload, List<ScenarioDraftValidationIssue> issues)
    {
        if (payload["buildingTypes"] is not JsonArray types || types.Count is 0 or > 10 || types.Any(t => Str(t) is not { } s || !Category().IsMatch(s)))
            issues.Add(new("FIELD_INVALID", "$.payload.buildingTypes", "1-10 building types such as school, hospital, office, residential."));
        if (payload.ContainsKey("keywords") && (payload["keywords"] is not JsonArray words || words.Count > 100 || words.Any(w => Str(w) is not { Length: > 0 and <= 50 })))
            issues.Add(new("FIELD_INVALID", "$.payload.keywords", "Up to 100 room-name tokens of 1-50 characters."));
        if (payload.ContainsKey("defaultScheme") && (Str(payload["defaultScheme"]) is not { } code || !LibraryCode().IsMatch(code)))
            issues.Add(new("FIELD_INVALID", "$.payload.defaultScheme", "The code of a ColorScheme Library item."));
        if (payload["materials"] is not JsonObject rooms || rooms.Count > MaxRooms) { issues.Add(new("FIELD_INVALID", "$.payload.materials", $"An object of at most {MaxRooms} room categories.")); return; }
        if (rooms[DefaultRoom] is not JsonObject defaults || Surfaces.Any(s => Str(defaults[s]) is null))
            issues.Add(new("FIELD_REQUIRED", "$.payload.materials.default", "Default floor, wall and ceiling materials are required."));
        foreach (var (room, node) in rooms)
        {
            var path = $"$.payload.materials.{room}";
            if (!Category().IsMatch(room)) issues.Add(new("FIELD_INVALID", path, "Room categories are lowercase identifiers."));
            if (node is not JsonObject surfaces || surfaces.Count == 0) { issues.Add(new("FIELD_INVALID", path, "Map floor, wall and/or ceiling to material keys.")); continue; }
            foreach (var (surface, material) in surfaces)
                if (!Surfaces.Contains(surface)) issues.Add(new("FIELD_UNKNOWN", $"{path}.{surface}", "Use floor, wall or ceiling."));
                else if (Str(material) is not { Length: <= 64 } key || !MaterialKey().IsMatch(key)) issues.Add(new("FIELD_INVALID", $"{path}.{surface}", "A material library key such as floor.tile."));
        }
    }

    /// <summary>ColorScheme payload: {description?, colors{wall, accentWall, doorWood, skirting}} as #RRGGBB.</summary>
    public static void ValidateScheme(JsonObject payload, List<ScenarioDraftValidationIssue> issues)
    {
        if (payload["colors"] is not JsonObject colors) { issues.Add(new("FIELD_REQUIRED", "$.payload.colors", "Colors wall, accentWall, doorWood and skirting are required.")); return; }
        foreach (var (name, _) in colors) if (!SchemeColors.Contains(name)) issues.Add(new("FIELD_UNKNOWN", $"$.payload.colors.{name}", "Use wall, accentWall, doorWood and skirting."));
        foreach (var name in SchemeColors)
            if (Str(colors[name]) is not { } hex || !HexColor().IsMatch(hex)) issues.Add(new("FIELD_INVALID", $"$.payload.colors.{name}", "A #RRGGBB colour."));
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
