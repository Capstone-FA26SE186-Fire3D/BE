using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;

namespace Fire3D.Application.Ifc;

// IFC-04: equipment the IFC worker detected (fire equipment, water sources, exits...) and OrganizationUser decisions on them.
/// <summary>Decision on a candidate. Confirmed keeps the detected catalogKey unless one is given; Retyped needs a catalogKey; Rejected drops it.</summary>
public sealed record EquipmentDecisionView(string Decision, string? CatalogKey, string? Note, Guid DecidedBy, DateTime DecidedAt);
/// <summary>A decision made on the same ifcGlobalId in the newest earlier revision of the Building; it is never applied until saved again.</summary>
public sealed record EquipmentSuggestionView(string Decision, string? CatalogKey, Guid FromRevisionId);
/// <summary>
/// One candidate from the building_metadata artifact of the accepted Geometry attempt. Position is [x, y, z] metres in the floor's frame,
/// Bbox is [width, depth, height] metres. NeedsConfirmation is true below confidence 0.9; the 3D model always comes from the shared library.
/// </summary>
public sealed record EquipmentCandidateView(string Id, string IfcGlobalId, string CatalogKey, string Source, string? IfcClass, decimal Confidence,
    string? FloorId, IReadOnlyList<decimal> Position, decimal? RotationY, IReadOnlyList<decimal>? Bbox, bool NeedsConfirmation,
    EquipmentDecisionView? Decision, EquipmentSuggestionView? SuggestedDecision);
/// <summary>
/// Version is the decision overlay version (ETag); 0 before the first save. MetadataAvailable is false when the worker sent no building_metadata.
/// RevisionStatus is the revision's current status. ExitConfirmationRequired is true while the accepted Geometry run reported
/// IFC_EXIT_UNKNOWN and no IfcDoor candidate is Confirmed/Retyped as door.exit: the revision stays NeedsFix until one is.
/// </summary>
public sealed record EquipmentCandidatesView(Guid RevisionId, Guid? ArtifactId, bool MetadataAvailable, long Version, IReadOnlyList<EquipmentCandidateView> Items,
    string? RevisionStatus = null, bool ExitConfirmationRequired = false);
public sealed record EquipmentDecisionInput(string CandidateId, string Decision, string? CatalogKey = null, string? Note = null);
public sealed record EquipmentDecisionsRequest(IReadOnlyList<EquipmentDecisionInput> Items);

public interface IEquipmentCandidates
{
    Task<AuthResult<EquipmentCandidatesView>> GetAsync(Guid actor, Guid family, Guid revision, CancellationToken ct);
    Task<AuthResult<EquipmentCandidatesView>> SaveAsync(Guid actor, Guid family, Guid revision, long expected, EquipmentDecisionsRequest request, CancellationToken ct);
}

public static partial class EquipmentDecisionRules
{
    public static readonly string[] Decisions = ["Confirmed", "Retyped", "Rejected"];
    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,99}$")] public static partial Regex CatalogKey();

    public static List<ScenarioDraftValidationIssue> Validate(EquipmentDecisionsRequest? request)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (request?.Items is not { Count: > 0 and <= 500 } items) { issues.Add(new("FIELD_REQUIRED", "$.items", "Send 1-500 decisions.")); return issues; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var path = $"$.items[{i}]"; var item = items[i];
            if (item is null) { issues.Add(new("FIELD_REQUIRED", path, "Required.")); continue; }
            if (item.CandidateId is not { Length: > 0 and <= 128 }) issues.Add(new("FIELD_REQUIRED", $"{path}.candidateId", "1-128 characters."));
            else if (!seen.Add(item.CandidateId)) issues.Add(new("ID_DUPLICATE", $"{path}.candidateId", "Decide each candidate once per request."));
            if (!Decisions.Contains(item.Decision)) issues.Add(new("VALUE_UNSUPPORTED", $"{path}.decision", "Confirmed, Retyped or Rejected."));
            if (item.Decision == "Retyped" && item.CatalogKey is null) issues.Add(new("FIELD_REQUIRED", $"{path}.catalogKey", "Retyped needs the new catalogKey."));
            if (item.CatalogKey is not null && (item.Decision == "Rejected" || !CatalogKey().IsMatch(item.CatalogKey)))
                issues.Add(new("VALUE_UNSUPPORTED", $"{path}.catalogKey", "A capability id such as extinguisher.portable; not allowed when Rejected."));
            if (item.Note is not null) issues.AddRange(Ai.OrganizationAiInput.Text(item.Note, $"{path}.note", 1000));
        }
        return issues;
    }
}

/// <summary>
/// Shape of the building_metadata artifact (contracts/ifc/v1/building-metadata.schema.json, schemaVersion fet3d.building/1). Interactables are
/// checked field by field because the backend serves them; spaces, portals, sources, materials and qaIssues only need their container shape.
/// </summary>
public static partial class BuildingMetadataValidator
{
    public const string SchemaVersion = "fet3d.building/1";
    private static readonly string[] Root = ["schemaVersion", "revisionId", "sources", "units", "storeys", "spaces", "portals", "interactables", "materials", "qaIssues"];
    private static readonly string[] Interactable = ["id", "ifcGlobalId", "catalogKey", "source", "ifcClass", "confidence", "confirmedBy", "floorId", "position", "rotationY", "bbox"];
    [GeneratedRegex("^[0-9A-Za-z_$]{22}\\z")] private static partial Regex GlobalId();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$")] private static partial Regex ItemId();

    public static string? DeclaredVersion(JsonNode? metadata) => metadata is JsonObject o && o["schemaVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static List<ScenarioDraftValidationIssue> Validate(JsonNode? metadata)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (metadata is not JsonObject root) { issues.Add(new("FIELD_REQUIRED", "$", "Metadata must be an object.")); return issues; }
        foreach (var key in root.Select(p => p.Key).Where(k => !Root.Contains(k))) issues.Add(new("FIELD_UNKNOWN", $"$.{key}", "Not part of fet3d.building/1."));
        if (DeclaredVersion(root) != SchemaVersion) issues.Add(new("FIELD_REQUIRED", "$.schemaVersion", $"Use \"{SchemaVersion}\"."));
        // A key sent as JSON null is invalid like any other wrong type; only an absent key means "not detected".
        foreach (var (name, max) in new[] { ("sources", 20), ("storeys", 500), ("spaces", 20000), ("portals", 20000), ("qaIssues", 5000) })
            if (root.ContainsKey(name) && (root[name] is not JsonArray array || array.Count > max || array.Any(x => x is not JsonObject)))
                issues.Add(new("FIELD_INVALID", $"$.{name}", $"An array of at most {max} objects."));
        if (root.ContainsKey("materials") && root["materials"] is not JsonObject) issues.Add(new("FIELD_INVALID", "$.materials", "An object."));
        // Space GlobalIds key the room facts (bim_facts), so each space appears once.
        if (root["spaces"] is JsonArray spaces)
        {
            var spaceIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < spaces.Count; i++)
                if (spaces[i] is JsonObject space && Str(space["ifcGlobalId"]) is { } spaceId && !spaceIds.Add(spaceId))
                    issues.Add(new("ID_DUPLICATE", $"$.spaces[{i}].ifcGlobalId", "Each IfcSpace GlobalId appears once."));
        }
        if (!root.ContainsKey("interactables")) return issues;
        if (root["interactables"] is not JsonArray list || list.Count > 5000) { issues.Add(new("FIELD_INVALID", "$.interactables", "An array of at most 5000 candidates.")); return issues; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var path = $"$.interactables[{i}]";
            if (list[i] is not JsonObject c) { issues.Add(new("FIELD_INVALID", path, "An object.")); continue; }
            foreach (var key in c.Select(p => p.Key).Where(k => !Interactable.Contains(k))) issues.Add(new("FIELD_UNKNOWN", $"{path}.{key}", "Not part of an interactable."));
            if (Str(c["id"]) is not { } id || !ItemId().IsMatch(id)) issues.Add(new("FIELD_INVALID", $"{path}.id", "1-128 letters, digits, _ . : -."));
            else if (!ids.Add(id)) issues.Add(new("ID_DUPLICATE", $"{path}.id", "Candidate ids are unique."));
            if (Str(c["ifcGlobalId"]) is not { } guid || !GlobalId().IsMatch(guid)) issues.Add(new("FIELD_INVALID", $"{path}.ifcGlobalId", "A 22-character IFC GlobalId."));
            if (Str(c["catalogKey"]) is not { } catalogKey || !EquipmentDecisionRules.CatalogKey().IsMatch(catalogKey)) issues.Add(new("FIELD_INVALID", $"{path}.catalogKey", "A capability id such as water.sink."));
            if (Str(c["source"]) is not ("ifc" or "inferred")) issues.Add(new("FIELD_INVALID", $"{path}.source", "ifc or inferred."));
            if (c["ifcClass"] is { } cls && Str(cls) is not { Length: > 0 and <= 100 }) issues.Add(new("FIELD_INVALID", $"{path}.ifcClass", "1-100 characters."));
            if (Num(c["confidence"]) is not (>= 0 and <= 1)) issues.Add(new("FIELD_INVALID", $"{path}.confidence", "A number from 0 to 1."));
            if (c["floorId"] is { } floor && Str(floor) is not { Length: > 0 and <= 128 }) issues.Add(new("FIELD_INVALID", $"{path}.floorId", "A floor id of preview_glb metadata."));
            if (!Vector(c["position"], positive: false)) issues.Add(new("FIELD_INVALID", $"{path}.position", $"[x, y, z] metres within ±{MaxMetres}."));
            if (c["rotationY"] is { } rotation && Num(rotation) is not (>= -360 and <= 360)) issues.Add(new("FIELD_INVALID", $"{path}.rotationY", "Degrees from -360 to 360."));
            if (c.ContainsKey("bbox") && !Vector(c["bbox"], positive: true)) issues.Add(new("FIELD_INVALID", $"{path}.bbox", $"[width, depth, height] positive metres up to {MaxMetres}."));
        }
        return issues;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static double? Num(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
    // Coordinates are served as decimals; a building larger than 100 km is not a valid model.
    private const double MaxMetres = 100_000;
    private static bool Vector(JsonNode? node, bool positive) => node is JsonArray a && a.Count == 3
        && a.All(x => Num(x) is { } n && Math.Abs(n) <= MaxMetres && (!positive || n > 0));
}
