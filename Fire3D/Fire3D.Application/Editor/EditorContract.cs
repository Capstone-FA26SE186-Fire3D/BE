using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;

namespace Fire3D.Application.Editor;

/// <summary>
/// Versioned editor contract shared by Geometry worker metadata, scenario drafts and runtime capability contracts.
/// Matrices are 16 finite numbers, column-major, multiplied with column vectors; GLB space is metres, Y-up, right-handed.
/// Documents without <c>schemaVersion</c> are legacy and are never reported as satisfying this contract.
/// </summary>
public static partial class EditorContract
{
    public const string V1 = "fet3d.editor/1";
    public static readonly string[] SupportedVersions = [V1];
    public static readonly string[] ObjectKinds = ["Spawn", "Hazard", "Goal", "Npc", "BlockedElement", "Equipment"];
    public static readonly string[] TrainingModes = ["Learn", "Guided", "Assessment"];
    // Behaviour objects must name a runtime capability; Unity behaviour is never inferred from the kind alone.
    public static readonly string[] CapabilityRequiredKinds = ["Hazard", "Npc", "Equipment"];
    public const int MaxFloors = 500, MaxMappings = 100_000, MaxObjects = 2_000;
    /// <summary>
    /// Rubric metrics the server can compute from learner telemetry (ExitReached, WrongExit, HazardExposure.amount, Moved.distanceMeters).
    /// A criterion on any other metric is rejected before release; scores are never taken from the client.
    /// </summary>
    public static readonly string[] LearnerMetrics = ["reached_exit", "completion_time_seconds", "wrong_exits", "hazard_exposure", "distance_meters"];

    /// <summary>Returns the declared schemaVersion, or null for a legacy document.</summary>
    public static string? DeclaredVersion(JsonNode? document) =>
        document is JsonObject o && o.TryGetPropertyValue("schemaVersion", out var v) ? (v is JsonValue s && s.TryGetValue<string>(out var text) ? text : "") : null;

    public static bool IsSupported(string? version) => version is not null && SupportedVersions.Contains(version, StringComparer.Ordinal);

    /// <summary>Applies a column-major affine matrix to a point: p' = M · [x,y,z,1]ᵀ.</summary>
    public static (double X, double Y, double Z) Apply(IReadOnlyList<double> m, double x, double y, double z) =>
        (m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14]);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$")] internal static partial Regex IdPattern();
    [GeneratedRegex("^[0-9A-Za-z_$]{22}$")] internal static partial Regex IfcGlobalIdPattern();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,99}$")] internal static partial Regex SemanticTypePattern();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$")] internal static partial Regex SemverPattern();
    [GeneratedRegex("^[0-9a-f]{64}$")] internal static partial Regex HashPattern();
}

/// <summary>Strict JSON reader that records every deviation as a JSONPath issue instead of throwing.</summary>
internal sealed class StrictReader(List<ScenarioDraftValidationIssue> issues)
{
    public List<ScenarioDraftValidationIssue> Issues { get; } = issues;
    public void Add(string code, string path, string message) => Issues.Add(new(code, path, message));

    public JsonObject? Object(JsonNode? node, string path, IReadOnlyCollection<string> allowed, bool required = true)
    {
        if (node is null) { if (required) Add("FIELD_REQUIRED", path, "An object is required."); return null; }
        if (node is not JsonObject o) { Add("TYPE_INVALID", path, "Must be an object."); return null; }
        foreach (var name in o.Select(p => p.Key))
            if (!allowed.Contains(name)) Add("FIELD_UNKNOWN", $"{path}.{name}", "Field is not defined by this schema version.");
        return o;
    }

    public JsonArray? Array(JsonNode? node, string path, int max, bool required = true)
    {
        if (node is null) { if (required) Add("FIELD_REQUIRED", path, "An array is required."); return null; }
        if (node is not JsonArray a) { Add("TYPE_INVALID", path, "Must be an array."); return null; }
        if (a.Count > max) { Add("ARRAY_TOO_LARGE", path, $"At most {max} entries are allowed."); return null; }
        return a;
    }

    public string? Text(JsonNode? node, string path, int max, bool required = true, Regex? pattern = null)
    {
        if (node is null) { if (required) Add("FIELD_REQUIRED", path, "A string is required."); return null; }
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String) { Add("TYPE_INVALID", path, "Must be a string."); return null; }
        var text = v.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl)) { Add("STRING_INVALID", path, $"Use 1-{max} printable characters."); return null; }
        if (pattern is not null && !pattern.IsMatch(text)) { Add("STRING_FORMAT_INVALID", path, "Value does not match the required format."); return null; }
        return text;
    }

    public double? Number(JsonNode? node, string path, bool required = true, double? min = null, double? max = null)
    {
        if (node is null) { if (required) Add("FIELD_REQUIRED", path, "A number is required."); return null; }
        // Parse the raw token so typed and parsed JsonValues behave the same; 1e400 parses to infinity and is rejected.
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number
            || !double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        { Add("NUMBER_INVALID", path, "Must be a finite number."); return null; }
        if (min is not null && number < min || max is not null && number > max)
        { Add("NUMBER_OUT_OF_RANGE", path, $"Must be between {min?.ToString(CultureInfo.InvariantCulture) ?? "-inf"} and {max?.ToString(CultureInfo.InvariantCulture) ?? "inf"}."); return null; }
        return number;
    }

    public long? Integer(JsonNode? node, string path, bool required = true, long min = long.MinValue, long max = long.MaxValue)
    {
        if (node is null) { if (required) Add("FIELD_REQUIRED", path, "An integer is required."); return null; }
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number
            || !long.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        { Add("INTEGER_INVALID", path, "Must be an integer."); return null; }
        if (number < min || number > max) { Add("NUMBER_OUT_OF_RANGE", path, $"Must be between {min} and {max}."); return null; }
        return number;
    }

    public bool? Boolean(JsonNode? node, string path, bool required = true)
    {
        if (node is null) { if (required) Add("FIELD_REQUIRED", path, "A boolean is required."); return null; }
        if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return v.GetValue<bool>();
        Add("TYPE_INVALID", path, "Must be a boolean."); return null;
    }

    public string? Constant(JsonNode? node, string path, string expected)
    {
        var text = Text(node, path, 100);
        if (text is not null && text != expected) { Add("VALUE_UNSUPPORTED", path, $"Only '{expected}' is supported."); return null; }
        return text;
    }

    public string? OneOf(JsonNode? node, string path, IReadOnlyCollection<string> values, bool required = true)
    {
        var text = Text(node, path, 100, required);
        if (text is not null && !values.Contains(text)) { Add("VALUE_UNSUPPORTED", path, $"Use one of: {string.Join(", ", values)}."); return null; }
        return text;
    }

    /// <summary>Reads a finite, affine (last row 0,0,0,1), invertible column-major 4x4 matrix.</summary>
    public double[]? Matrix(JsonNode? node, string path)
    {
        var array = Array(node, path, 16);
        if (array is null) return null;
        if (array.Count != 16) { Add("MATRIX_INVALID", path, "A matrix must contain exactly 16 numbers."); return null; }
        var m = new double[16];
        for (var i = 0; i < 16; i++)
        {
            var value = Number(array[i], $"{path}[{i}]");
            if (value is null) return null;
            m[i] = value.Value;
        }
        if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1)
        { Add("MATRIX_NOT_AFFINE", path, "Column-major matrix row 4 must be 0,0,0,1 (indices 3, 7, 11, 15)."); return null; }
        var det = m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-12) { Add("MATRIX_SINGULAR", path, "Matrix must be invertible."); return null; }
        return m;
    }
}

/// <summary>Validates Geometry preview metadata emitted by the worker before the output is accepted.</summary>
public static class GeometryMetadataValidator
{
    private static readonly string[] Root = ["schemaVersion", "units", "upAxis", "handedness", "coordinateTransform", "floors", "semanticMapping"];
    private static readonly string[] Floor = ["id", "name", "elevationMeters", "ifcGlobalId", "transform"];
    private static readonly string[] Mapping = ["ifcGlobalId", "nodeId", "floorId", "semanticType"];

    public static IReadOnlyList<ScenarioDraftValidationIssue> Validate(JsonNode? metadata)
    {
        var r = new StrictReader([]);
        var root = r.Object(metadata, "$", Root);
        if (root is null) return r.Issues;
        if (r.Constant(root["schemaVersion"], "$.schemaVersion", EditorContract.V1) is null) return r.Issues;
        r.Constant(root["units"], "$.units", "m");
        r.Constant(root["upAxis"], "$.upAxis", "Y");
        r.Constant(root["handedness"], "$.handedness", "right");
        r.Matrix(root["coordinateTransform"], "$.coordinateTransform");

        var floorIds = new HashSet<string>(StringComparer.Ordinal);
        var floorGuids = new HashSet<string>(StringComparer.Ordinal);
        if (r.Array(root["floors"], "$.floors", EditorContract.MaxFloors) is { } floors)
        {
            if (floors.Count == 0) r.Add("FLOORS_REQUIRED", "$.floors", "At least one floor is required.");
            for (var i = 0; i < floors.Count; i++)
            {
                var path = $"$.floors[{i}]";
                var floor = r.Object(floors[i], path, Floor);
                if (floor is null) continue;
                if (r.Text(floor["id"], $"{path}.id", 128, pattern: EditorContract.IdPattern()) is { } id && !floorIds.Add(id))
                    r.Add("ID_DUPLICATE", $"{path}.id", "Floor IDs must be unique within the revision.");
                r.Text(floor["name"], $"{path}.name", 255);
                r.Number(floor["elevationMeters"], $"{path}.elevationMeters", min: -10_000, max: 10_000);
                if (floor["ifcGlobalId"] is not null && r.Text(floor["ifcGlobalId"], $"{path}.ifcGlobalId", 22, pattern: EditorContract.IfcGlobalIdPattern()) is { } guid && !floorGuids.Add(guid))
                    r.Add("ID_DUPLICATE", $"{path}.ifcGlobalId", "A floor IFC GlobalId may appear once.");
                r.Matrix(floor["transform"], $"{path}.transform");
            }
        }

        if (r.Array(root["semanticMapping"], "$.semanticMapping", EditorContract.MaxMappings) is { } mappings)
        {
            var globals = new HashSet<string>(StringComparer.Ordinal);
            var nodes = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < mappings.Count; i++)
            {
                var path = $"$.semanticMapping[{i}]";
                var mapping = r.Object(mappings[i], path, Mapping);
                if (mapping is null) continue;
                if (r.Text(mapping["ifcGlobalId"], $"{path}.ifcGlobalId", 22, pattern: EditorContract.IfcGlobalIdPattern()) is { } guid && !globals.Add(guid))
                    r.Add("ID_DUPLICATE", $"{path}.ifcGlobalId", "Each IFC GlobalId maps to one GLB node.");
                if (r.Text(mapping["nodeId"], $"{path}.nodeId", 256) is { } node && !nodes.Add(node))
                    r.Add("ID_DUPLICATE", $"{path}.nodeId", "Each GLB node maps to one IFC element.");
                if (r.Text(mapping["floorId"], $"{path}.floorId", 128, pattern: EditorContract.IdPattern()) is { } floorId && floorIds.Count > 0 && !floorIds.Contains(floorId))
                    r.Add("FLOOR_NOT_FOUND", $"{path}.floorId", "Mapping must reference a floor declared in this metadata.");
                r.Text(mapping["semanticType"], $"{path}.semanticType", 100, pattern: EditorContract.SemanticTypePattern());
            }
        }
        return r.Issues;
    }
}

/// <summary>Capability contract published in the runtime catalog for one capability ID.</summary>
public sealed record CapabilityContract(string Id, string Version, IReadOnlyList<string> ObjectKinds, JsonObject? Parameters);

public static class CapabilityContracts
{
    private static readonly string[] Contract = ["version", "objectKinds", "parameters", "description"];

    /// <summary>
    /// Reads the catalog contract map. A capability without a valid contract is omitted so editor objects cannot use it;
    /// legacy runtime capability lists remain valid for package compatibility checks.
    /// </summary>
    public static IReadOnlyDictionary<string, CapabilityContract> Parse(JsonNode? capabilities, JsonNode? contracts, List<ScenarioDraftValidationIssue>? issues = null)
    {
        var result = new Dictionary<string, CapabilityContract>(StringComparer.Ordinal);
        var declared = capabilities is JsonArray list
            ? list.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(x => x is not null).ToHashSet(StringComparer.Ordinal)
            : [];
        if (contracts is not JsonObject map) return result;
        foreach (var (id, node) in map)
        {
            var r = new StrictReader(issues ?? []);
            var before = r.Issues.Count;
            var path = $"$.capabilityContracts.{id}";
            if (!EditorContract.IdPattern().IsMatch(id) || !declared.Contains(id)) { r.Add("CAPABILITY_CONTRACT_INVALID", path, "Contract must belong to a capability listed by this runtime."); continue; }
            var o = r.Object(node, path, Contract);
            if (o is null) continue;
            var version = r.Text(o["version"], $"{path}.version", 32, pattern: EditorContract.SemverPattern());
            var kinds = new List<string>();
            if (r.Array(o["objectKinds"], $"{path}.objectKinds", EditorContract.ObjectKinds.Length) is { } array)
            {
                if (array.Count == 0) r.Add("FIELD_REQUIRED", $"{path}.objectKinds", "At least one object kind is required.");
                for (var i = 0; i < array.Count; i++)
                    if (r.OneOf(array[i], $"{path}.objectKinds[{i}]", EditorContract.ObjectKinds) is { } kind)
                    {
                        if (kinds.Contains(kind)) r.Add("ID_DUPLICATE", $"{path}.objectKinds[{i}]", "Object kinds must be unique.");
                        else kinds.Add(kind);
                    }
            }
            if (o["description"] is not null) r.Text(o["description"], $"{path}.description", 1000);
            JsonObject? parameters = null;
            if (o["parameters"] is not null && ParameterSchema.ValidateSchema(o["parameters"], $"{path}.parameters", r)) parameters = (JsonObject)o["parameters"]!;
            if (r.Issues.Count == before && version is not null) result[id] = new(id, version, kinds, parameters);
        }
        return result;
    }
}

/// <summary>Minimal, closed JSON-Schema subset for capability parameters.</summary>
public static class ParameterSchema
{
    private static readonly string[] Root = ["type", "properties", "required", "additionalProperties"];
    private static readonly string[] Property = ["type", "minimum", "maximum", "enum", "maxLength", "description"];
    private static readonly string[] Types = ["number", "integer", "string", "boolean"];

    internal static bool ValidateSchema(JsonNode? schema, string path, StrictReader r)
    {
        var before = r.Issues.Count;
        var o = r.Object(schema, path, Root);
        if (o is null) return false;
        r.Constant(o["type"], $"{path}.type", "object");
        if (r.Boolean(o["additionalProperties"], $"{path}.additionalProperties") is true)
            r.Add("VALUE_UNSUPPORTED", $"{path}.additionalProperties", "Parameter schemas must be closed (false).");
        var props = r.Object(o["properties"], $"{path}.properties", o["properties"] is JsonObject p ? p.Select(x => x.Key).ToArray() : []);
        if (props is not null)
            foreach (var (name, node) in props)
            {
                var pp = $"{path}.properties.{name}";
                if (!EditorContract.IdPattern().IsMatch(name)) r.Add("STRING_FORMAT_INVALID", pp, "Parameter names use the editor ID format.");
                var prop = r.Object(node, pp, Property);
                if (prop is null) continue;
                var type = r.OneOf(prop["type"], $"{pp}.type", Types);
                if (prop["minimum"] is not null) r.Number(prop["minimum"], $"{pp}.minimum");
                if (prop["maximum"] is not null) r.Number(prop["maximum"], $"{pp}.maximum");
                if (prop["maxLength"] is not null) r.Integer(prop["maxLength"], $"{pp}.maxLength", min: 1, max: 10_000);
                if (prop["description"] is not null) r.Text(prop["description"], $"{pp}.description", 1000);
                if (prop["enum"] is not null && r.Array(prop["enum"], $"{pp}.enum", 100) is { Count: 0 })
                    r.Add("FIELD_REQUIRED", $"{pp}.enum", "Enum must list at least one value.");
                if (type is "boolean" && (prop["minimum"] ?? prop["maximum"] ?? prop["maxLength"]) is not null)
                    r.Add("VALUE_UNSUPPORTED", pp, "Boolean parameters cannot declare numeric or length limits.");
            }
        if (o["required"] is not null && r.Array(o["required"], $"{path}.required", 100) is { } required)
            for (var i = 0; i < required.Count; i++)
                if (r.Text(required[i], $"{path}.required[{i}]", 128) is { } name && props?.ContainsKey(name) != true)
                    r.Add("FIELD_UNKNOWN", $"{path}.required[{i}]", "Required parameters must be declared in properties.");
        return r.Issues.Count == before;
    }

    /// <summary>Validates object parameters against an already-validated schema; null schema permits no parameters.</summary>
    internal static void ValidateValue(JsonNode? value, JsonObject? schema, string path, StrictReader r)
    {
        var props = schema?["properties"] as JsonObject;
        var o = r.Object(value, path, props?.Select(x => x.Key).ToArray() ?? [], required: false);
        if (schema is null)
        {
            if (o is { Count: > 0 }) r.Add("PARAMETERS_NOT_ALLOWED", path, "This object has no capability parameter schema.");
            return;
        }
        foreach (var name in (schema["required"] as JsonArray)?.Select(x => x!.GetValue<string>()) ?? [])
            if (o?.ContainsKey(name) != true) r.Add("FIELD_REQUIRED", $"{path}.{name}", "Parameter is required by the capability contract.");
        if (o is null || props is null) return;
        foreach (var (name, node) in o)
        {
            if (props[name] is not JsonObject prop) continue;
            var pp = $"{path}.{name}";
            var type = prop["type"]!.GetValue<string>();
            double? number = type switch { "number" => r.Number(node, pp), "integer" => r.Integer(node, pp), _ => null };
            var accepted = type switch
            {
                "number" or "integer" => number is not null,
                "string" => r.Text(node, pp, prop["maxLength"] is JsonValue length ? int.Parse(length.ToJsonString(), CultureInfo.InvariantCulture) : 10_000) is not null,
                _ => r.Boolean(node, pp) is not null,
            };
            if (!accepted) continue;
            if (number is not null && (prop["minimum"] is JsonValue min && number < double.Parse(min.ToJsonString(), CultureInfo.InvariantCulture)
                || prop["maximum"] is JsonValue max && number > double.Parse(max.ToJsonString(), CultureInfo.InvariantCulture)))
                r.Add("PARAMETER_OUT_OF_RANGE", pp, "Parameter is outside the capability contract range.");
            if (prop["enum"] is JsonArray options && !options.Any(x => x?.ToJsonString() == node?.ToJsonString()))
                r.Add("PARAMETER_NOT_ALLOWED", pp, "Parameter value is not listed by the capability contract.");
        }
    }
}

/// <summary>Validates scenario draft state written with <see cref="EditorContract.V1"/>.</summary>
public static class ScenarioStateV1Validator
{
    private static readonly string[] Root = ["schemaVersion", "name", "geometry", "runtimeVersion", "objects", "timeLimitSeconds", "rubric",
        "learningObjectives", "learnerInstructions", "modePolicy", "randomSeed", "replanIntervalSeconds"];
    private static readonly string[] Geometry = ["artifactId", "sha256Hash"];
    private static readonly string[] Object = ["id", "kind", "placement", "anchorId", "capability", "parameters", "label"];
    private static readonly string[] Placement = ["floorId", "position", "rotation"];
    private static readonly string[] Vector = ["x", "y", "z"];
    private static readonly string[] Quaternion = ["x", "y", "z", "w"];
    private static readonly string[] Capability = ["id", "version"];
    private static readonly string[] Mode = ["allowedModes"];
    private static readonly string[] Rubric = ["schema_version", "pass_threshold", "criteria"];
    private static readonly string[] Criterion = ["id", "metric", "mandatory", "weight", "threshold", "operator", "description"];

    /// <summary>
    /// Shape checks used by PUT: types, unknown fields, finite numbers, duplicate IDs and enum values.
    /// Partial drafts are allowed; completeness, revision references and capability parameters are checked by validate/snapshot.
    /// </summary>
    public static IReadOnlyList<ScenarioDraftValidationIssue> ValidateShape(JsonNode? state) => Run(state, complete: false, null);

    /// <summary>Complete validation used before snapshot. Revision references are re-checked by the database gate under lock.</summary>
    public static IReadOnlyList<ScenarioDraftValidationIssue> ValidateComplete(JsonNode? state, IReadOnlyDictionary<string, CapabilityContract>? contracts) => Run(state, complete: true, contracts);

    private static IReadOnlyList<ScenarioDraftValidationIssue> Run(JsonNode? state, bool complete, IReadOnlyDictionary<string, CapabilityContract>? contracts)
    {
        var r = new StrictReader([]);
        var root = r.Object(state, "$", Root);
        if (root is null || r.Constant(root["schemaVersion"], "$.schemaVersion", EditorContract.V1) is null) return r.Issues;
        if (root["name"] is not null) r.Text(root["name"], "$.name", 255);
        if (r.Object(root["geometry"], "$.geometry", Geometry, complete) is { } geometry)
        {
            if (r.Text(geometry["artifactId"], "$.geometry.artifactId", 36) is { } id && !Guid.TryParse(id, out _))
                r.Add("STRING_FORMAT_INVALID", "$.geometry.artifactId", "Use the accepted Geometry artifact UUID.");
            r.Text(geometry["sha256Hash"], "$.geometry.sha256Hash", 64, pattern: EditorContract.HashPattern());
        }
        var runtime = r.Text(root["runtimeVersion"], "$.runtimeVersion", 32, complete, EditorContract.SemverPattern());
        if (root["timeLimitSeconds"] is not null || complete) r.Integer(root["timeLimitSeconds"], "$.timeLimitSeconds", min: 1, max: 86_400);
        if (root["randomSeed"] is not null) r.Integer(root["randomSeed"], "$.randomSeed");
        if (root["replanIntervalSeconds"] is not null) r.Integer(root["replanIntervalSeconds"], "$.replanIntervalSeconds", min: 0, max: 3_600);
        if (r.Object(root["modePolicy"], "$.modePolicy", Mode, required: false) is { } mode)
            UniqueValues(r, mode["allowedModes"], "$.modePolicy.allowedModes", EditorContract.TrainingModes.Length, x => r.OneOf(x.node, x.path, EditorContract.TrainingModes), requireOne: true);
        if (root["learningObjectives"] is not null || complete)
            UniqueValues(r, root["learningObjectives"], "$.learningObjectives", 50, x => r.Text(x.node, x.path, 1000), requireOne: complete);
        if (root["learnerInstructions"] is not null || complete) r.Text(root["learnerInstructions"], "$.learnerInstructions", 10_000);
        ValidateRubric(r, root["rubric"], complete);

        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        if (r.Array(root["objects"], "$.objects", EditorContract.MaxObjects, complete) is { } objects)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < objects.Count; i++)
            {
                var path = $"$.objects[{i}]";
                var o = r.Object(objects[i], path, Object);
                if (o is null) continue;
                if (r.Text(o["id"], $"{path}.id", 128, pattern: EditorContract.IdPattern()) is { } id && !ids.Add(id))
                    r.Add("ID_DUPLICATE", $"{path}.id", "Object IDs must be unique within the draft.");
                var kind = r.OneOf(o["kind"], $"{path}.kind", EditorContract.ObjectKinds);
                if (kind is not null) kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                if (o["label"] is not null) r.Text(o["label"], $"{path}.label", 255);
                if (r.Object(o["placement"], $"{path}.placement", Placement) is { } placement)
                {
                    r.Text(placement["floorId"], $"{path}.placement.floorId", 128, pattern: EditorContract.IdPattern());
                    if (r.Object(placement["position"], $"{path}.placement.position", Vector) is { } position)
                        foreach (var axis in Vector) r.Number(position[axis], $"{path}.placement.position.{axis}", min: -100_000, max: 100_000);
                    if (r.Object(placement["rotation"], $"{path}.placement.rotation", Quaternion) is { } rotation)
                    {
                        var parts = Quaternion.Select(axis => r.Number(rotation[axis], $"{path}.placement.rotation.{axis}", min: -1, max: 1)).ToArray();
                        if (parts.All(x => x is not null) && Math.Abs(Math.Sqrt(parts.Sum(x => x!.Value * x.Value)) - 1) > 1e-3)
                            r.Add("QUATERNION_NOT_NORMALIZED", $"{path}.placement.rotation", "Rotation must be a unit quaternion (|q| = 1 ± 0.001).");
                    }
                }
                if (o["anchorId"] is not null) r.Text(o["anchorId"], $"{path}.anchorId", 22, pattern: EditorContract.IfcGlobalIdPattern());
                else if (complete && kind == "BlockedElement") r.Add("ANCHOR_REQUIRED", $"{path}.anchorId", "A blocked element must reference an IFC element.");
                CapabilityContract? contract = null;
                if (r.Object(o["capability"], $"{path}.capability", Capability, required: false) is { } capability)
                {
                    var capabilityId = r.Text(capability["id"], $"{path}.capability.id", 128, pattern: EditorContract.IdPattern());
                    var version = r.Text(capability["version"], $"{path}.capability.version", 32, pattern: EditorContract.SemverPattern());
                    if (complete && capabilityId is not null && version is not null)
                    {
                        if (runtime is null || contracts is null || !contracts.TryGetValue(capabilityId, out contract))
                            r.Add("CAPABILITY_NOT_SUPPORTED", $"{path}.capability.id", "The selected runtime does not publish a contract for this capability.");
                        else if (contract.Version != version) { r.Add("CAPABILITY_VERSION_MISMATCH", $"{path}.capability.version", $"Runtime publishes version {contract.Version}."); contract = null; }
                        else if (kind is not null && !contract.ObjectKinds.Contains(kind)) { r.Add("CAPABILITY_KIND_UNSUPPORTED", $"{path}.capability.id", "Capability does not support this object kind."); contract = null; }
                    }
                }
                else if (complete && kind is not null && EditorContract.CapabilityRequiredKinds.Contains(kind))
                    r.Add("CAPABILITY_REQUIRED", $"{path}.capability", "Behaviour objects must select a runtime capability.");
                if (!complete) { r.Object(o["parameters"], $"{path}.parameters", o["parameters"] is JsonObject p ? p.Select(x => x.Key).ToArray() : [], required: false); continue; }
                if (contract is not null || o["capability"] is null) ParameterSchema.ValidateValue(o["parameters"], contract?.Parameters, $"{path}.parameters", r);
            }
        }
        if (complete)
        {
            if (kinds.GetValueOrDefault("Spawn") == 0) r.Add("SPAWN_REQUIRED", "$.objects", "At least one Spawn object is required.");
            if (kinds.GetValueOrDefault("Goal") == 0) r.Add("GOAL_REQUIRED", "$.objects", "At least one Goal object is required.");
        }
        return r.Issues;
    }

    private static void UniqueValues(StrictReader r, JsonNode? node, string path, int max, Func<(JsonNode? node, string path), string?> read, bool requireOne)
    {
        var array = r.Array(node, path, max);
        if (array is null) return;
        if (requireOne && array.Count == 0) r.Add("FIELD_REQUIRED", path, "At least one value is required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count; i++)
            if (read((array[i], $"{path}[{i}]")) is { } value && !seen.Add(value)) r.Add("ID_DUPLICATE", $"{path}[{i}]", "Values must be unique.");
    }

    private static void ValidateRubric(StrictReader r, JsonNode? node, bool complete)
    {
        var rubric = r.Object(node, "$.rubric", Rubric, complete);
        if (rubric is null) return;
        r.Text(rubric["schema_version"], "$.rubric.schema_version", 32);
        r.Number(rubric["pass_threshold"], "$.rubric.pass_threshold");
        var criteria = r.Array(rubric["criteria"], "$.rubric.criteria", 100);
        if (criteria is null) return;
        if (complete && criteria.Count == 0) r.Add("FIELD_REQUIRED", "$.rubric.criteria", "At least one criterion is required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < criteria.Count; i++)
        {
            var path = $"$.rubric.criteria[{i}]";
            var c = r.Object(criteria[i], path, Criterion);
            if (c is null) continue;
            if (r.Text(c["id"], $"{path}.id", 128, pattern: EditorContract.IdPattern()) is { } id && !ids.Add(id))
                r.Add("ID_DUPLICATE", $"{path}.id", "Criterion IDs must be unique.");
            if (r.Text(c["metric"], $"{path}.metric", 128, pattern: EditorContract.IdPattern()) is { } metric && complete && !EditorContract.LearnerMetrics.Contains(metric))
                r.Add("RUBRIC_METRIC_UNSUPPORTED", $"{path}.metric", $"Use a server-computed metric: {string.Join(", ", EditorContract.LearnerMetrics)}.");
            r.Boolean(c["mandatory"], $"{path}.mandatory");
            r.Number(c["weight"], $"{path}.weight", min: 0);
            r.Number(c["threshold"], $"{path}.threshold");
            r.OneOf(c["operator"], $"{path}.operator", ["gte", "lte", "eq"]);
            if (c["description"] is not null) r.Text(c["description"], $"{path}.description", 1000);
        }
    }
}
