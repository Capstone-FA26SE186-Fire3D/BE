using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;

namespace Fire3D.Application.FireSafety;

/// <summary>
/// One version of the fire infrastructure of an IFC revision (BE#213, FR-PLAN-04). Status Draft -> Submitted -> Approved | Rejected;
/// Approved is the lock. Revision is the ETag (If-Match). Items are editor objects plus category (see <see cref="FireInfrastructureRules"/>).
/// </summary>
public sealed record FireInfrastructureResponse(Guid Id, Guid RevisionId, Guid BuildingId, string? BuildingName, Guid OrganizationId, int Version,
    string Status, long Revision, JsonNode? Geometry, string? RuntimeVersion, JsonNode Items, string? ContentHash, Guid CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? SubmittedBy, string? SubmittedByName, DateTimeOffset? SubmittedAt,
    Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionReason, int ScenarioDraftCount);
/// <summary>Every version of one revision, newest first.</summary>
public sealed record FireInfrastructureListResponse(Guid RevisionId, IReadOnlyList<FireInfrastructureResponse> Items);
/// <summary>PUT body: replaces geometry pin, runtime version and items of a Draft.</summary>
public sealed record UpdateFireInfrastructureRequest(JsonNode? Geometry, string? RuntimeVersion, JsonNode? Items);
/// <summary>Submit readiness of the stored content: the same checks submit runs.</summary>
public sealed record FireInfrastructureValidationResponse(Guid Id, long Revision, bool IsValid, IReadOnlyList<ScenarioDraftValidationIssue> Issues);

public interface IFireInfrastructureStore
{
    /// <summary>fire_infrastructure_gate: List/Create (resource = revision), Get/Update/Submit (resource = infrastructure).</summary>
    Task<AuthResult<JsonElement>> ExecuteAsync(string action, Guid actor, Guid? family, Guid resource, object input, string? key, long? expected, CancellationToken ct);
}

/// <summary>
/// Item rules. An item is a fet3d.editor/1 object plus category: Exit and AssemblyPoint are Goal objects; Equipment and WaterSource are
/// Equipment objects with a fixed capability (not a scenario item, not the building-only elevator). Exits are required to submit.
/// </summary>
public static class FireInfrastructureRules
{
    public static readonly string[] Categories = ["Equipment", "WaterSource", "Exit", "AssemblyPoint"];
    /// <summary>
    /// Equipment a scenario places itself; everything else of kind Equipment belongs to the infrastructure. door.exit_locked and
    /// door.fire_wedged (BE#224) are the state of an existing IFC door in one situation (anchorId = the door), not fixed equipment.
    /// </summary>
    public static readonly string[] ScenarioItemCapabilities = ["item.towel", "bait.belongings", "door.exit_locked", "door.fire_wedged"];

    public static List<ScenarioDraftValidationIssue> Validate(JsonNode? geometry, string? runtimeVersion, JsonNode? items, bool complete,
        IReadOnlyDictionary<string, CapabilityContract>? contracts)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        if (items is not JsonArray array) { issues.Add(new("FIELD_REQUIRED", "$.items", "items must be an array of infrastructure items.")); return issues; }
        var exits = 0;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject o) continue;
            var path = $"$.items[{i}]";
            var category = o["category"] is JsonValue c && c.TryGetValue<string>(out var text) ? text : null;
            var kind = o["kind"] is JsonValue k && k.TryGetValue<string>(out var kindText) ? kindText : null;
            var capability = o["capability"]?["id"] is JsonValue cap && cap.TryGetValue<string>(out var capText) ? capText : null;
            if (category is null || !Categories.Contains(category, StringComparer.Ordinal))
            {
                issues.Add(new("FIELD_INVALID", path + ".category", "category must be Equipment, WaterSource, Exit or AssemblyPoint."));
                continue;
            }
            if (category == "Exit") exits++;
            if (category is "Exit" or "AssemblyPoint" && kind != "Goal")
                issues.Add(new("INFRASTRUCTURE_KIND_MISMATCH", path + ".kind", $"A {category} item is a Goal object."));
            if (category is "Equipment" or "WaterSource")
            {
                if (kind != "Equipment") issues.Add(new("INFRASTRUCTURE_KIND_MISMATCH", path + ".kind", $"A {category} item is an Equipment object."));
                else if (capability is not null && (ScenarioItemCapabilities.Contains(capability) || capability == EditorContract.BuildingOnlyCapability))
                    issues.Add(new("INFRASTRUCTURE_CAPABILITY_NOT_FIXED", path + ".capability.id",
                        capability == EditorContract.BuildingOnlyCapability ? "Elevators come from the IFC drawing."
                            : "Scenario items and door states (locked exit, wedged fire door) belong to scenarios, not to the infrastructure."));
            }
        }
        var raw = complete ? ScenarioStateV1Validator.ValidateComplete(Document(geometry, runtimeVersion, array), contracts)
            : ScenarioStateV1Validator.ValidateShape(Document(geometry, runtimeVersion, array));
        foreach (var issue in raw)
            if (issue.Path.StartsWith("$.objects[", StringComparison.Ordinal) || (issue.Path == "$.objects" && issue.Code is not ("SPAWN_REQUIRED" or "GOAL_REQUIRED"))
                || issue.Path.StartsWith("$.geometry", StringComparison.Ordinal) || issue.Path.StartsWith("$.runtimeVersion", StringComparison.Ordinal))
                issues.Add(issue with { Path = ItemPath(issue.Path) });
        if (complete && exits == 0) issues.Add(new("EXIT_REQUIRED", "$.items", "At least one Exit item is required."));
        return issues;
    }

    /// <summary>The items as an editor document (category removed) for the shared object and reference checks.</summary>
    public static JsonObject Document(JsonNode? geometry, string? runtimeVersion, JsonArray items)
    {
        var objects = new JsonArray();
        foreach (var item in items)
        {
            var copy = item?.DeepClone();
            if (copy is JsonObject o) o.Remove("category");
            objects.Add(copy);
        }
        var document = new JsonObject { ["schemaVersion"] = EditorContract.V1, ["objects"] = objects };
        if (geometry is not null) document["geometry"] = geometry.DeepClone();
        if (runtimeVersion is not null) document["runtimeVersion"] = runtimeVersion;
        return document;
    }

    public static string ItemPath(string path) => path.StartsWith("$.objects", StringComparison.Ordinal) ? "$.items" + path["$.objects".Length..] : path;
}

public interface IFireInfrastructureService
{
    Task<AuthResult<FireInfrastructureListResponse>> ListAsync(Guid actor, Guid? family, Guid revision, CancellationToken ct);
    Task<AuthResult<FireInfrastructureResponse>> CreateAsync(Guid actor, Guid? family, Guid revision, string? key, CancellationToken ct);
    Task<AuthResult<FireInfrastructureResponse>> GetAsync(Guid actor, Guid? family, Guid id, CancellationToken ct);
    Task<AuthResult<FireInfrastructureResponse>> UpdateAsync(Guid actor, Guid? family, Guid id, long expected, UpdateFireInfrastructureRequest request, CancellationToken ct);
    Task<AuthResult<FireInfrastructureValidationResponse>> ValidateAsync(Guid actor, Guid? family, Guid id, CancellationToken ct);
    Task<AuthResult<FireInfrastructureResponse>> SubmitAsync(Guid actor, Guid? family, Guid id, long expected, string? key, CancellationToken ct);
}

public sealed class FireInfrastructureService(IFireInfrastructureStore store, IEquipmentCandidates equipment, IRuntimeCatalogReadStore catalog,
    IScenarioReadStore references) : IFireInfrastructureService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AuthResult<FireInfrastructureListResponse>> ListAsync(Guid actor, Guid? family, Guid revision, CancellationToken ct) =>
        Map<FireInfrastructureListResponse>(await store.ExecuteAsync("List", actor, family, revision, new { }, null, null, ct));

    /// <summary>
    /// New Draft version. The first version starts from the revision's confirmed equipment decisions (fixed equipment and water sources,
    /// placed at the detected position, capability versions of the newest active runtime); later versions copy the newest version.
    /// Exits and assembly points are added by the organization.
    /// </summary>
    public async Task<AuthResult<FireInfrastructureResponse>> CreateAsync(Guid actor, Guid? family, Guid revision, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return AuthResult<FireInfrastructureResponse>.Fail("IDEMPOTENCY_KEY_REQUIRED", "Send an Idempotency-Key header.", 400);
        var seed = await SeedAsync(actor, family, revision, ct);
        return Map<FireInfrastructureResponse>(await store.ExecuteAsync("Create", actor, family, revision, new { seed }, key, null, ct));
    }

    public async Task<AuthResult<FireInfrastructureResponse>> GetAsync(Guid actor, Guid? family, Guid id, CancellationToken ct) =>
        Map<FireInfrastructureResponse>(await store.ExecuteAsync("Get", actor, family, id, new { }, null, null, ct));

    public async Task<AuthResult<FireInfrastructureResponse>> UpdateAsync(Guid actor, Guid? family, Guid id, long expected, UpdateFireInfrastructureRequest request, CancellationToken ct)
    {
        var runtime = string.IsNullOrWhiteSpace(request.RuntimeVersion) ? null : request.RuntimeVersion.Trim();
        var issues = FireInfrastructureRules.Validate(request.Geometry, runtime, request.Items, complete: false, null);
        if (issues.Count > 0) return Invalid<FireInfrastructureResponse>(issues);
        return Map<FireInfrastructureResponse>(await store.ExecuteAsync("Update", actor, family, id,
            new { geometry = request.Geometry, runtimeVersion = runtime, items = request.Items }, null, expected, ct));
    }

    public async Task<AuthResult<FireInfrastructureValidationResponse>> ValidateAsync(Guid actor, Guid? family, Guid id, CancellationToken ct)
    {
        var current = await GetAsync(actor, family, id, ct);
        if (!current.IsSuccess) return new(default, current.Error);
        var issues = await CompleteIssuesAsync(current.Value!, ct);
        return AuthResult<FireInfrastructureValidationResponse>.Ok(new(id, current.Value!.Revision, issues.Count == 0, issues));
    }

    public async Task<AuthResult<FireInfrastructureResponse>> SubmitAsync(Guid actor, Guid? family, Guid id, long expected, string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return AuthResult<FireInfrastructureResponse>.Fail("IDEMPOTENCY_KEY_REQUIRED", "Send an Idempotency-Key header.", 400);
        var current = await GetAsync(actor, family, id, ct);
        if (!current.IsSuccess) return current;
        // Only the content the client saw (If-Match) is validated; a stale revision, a replay or a decided version is left to the gate.
        if (current.Value!.Revision == expected && current.Value.Status == "Draft" && await CompleteIssuesAsync(current.Value, ct) is { Count: > 0 } issues)
            return Invalid<FireInfrastructureResponse>(issues);
        return Map<FireInfrastructureResponse>(await store.ExecuteAsync("Submit", actor, family, id, new { }, key, expected, ct));
    }

    private async Task<List<ScenarioDraftValidationIssue>> CompleteIssuesAsync(FireInfrastructureResponse infrastructure, CancellationToken ct)
    {
        var runtime = infrastructure.RuntimeVersion is null ? null : await catalog.GetActiveRuntimeAsync(infrastructure.RuntimeVersion, ct);
        var issues = FireInfrastructureRules.Validate(infrastructure.Geometry, infrastructure.RuntimeVersion, infrastructure.Items, complete: true, RuntimeCatalogContracts.For(runtime));
        if (infrastructure.Items is JsonArray items)
            foreach (var issue in await references.ValidateReferencesAsync(infrastructure.RevisionId,
                FireInfrastructureRules.Document(infrastructure.Geometry, infrastructure.RuntimeVersion, items), ct))
            {
                var mapped = issue with { Path = FireInfrastructureRules.ItemPath(issue.Path) };
                if (!issues.Any(x => x.Code == mapped.Code && x.Path == mapped.Path)) issues.Add(mapped);
            }
        return issues;
    }

    private async Task<object> SeedAsync(Guid actor, Guid? family, Guid revision, CancellationToken ct)
    {
        var runtimes = await catalog.GetActiveCatalogAsync(ct);
        var runtime = runtimes.OrderByDescending(x => Version.TryParse(x.RuntimeVersion, out var v) ? v : new Version(0, 0)).FirstOrDefault();
        var contracts = RuntimeCatalogContracts.For(runtime);
        var items = new JsonArray();
        var candidates = family is Guid f ? await equipment.GetAsync(actor, f, revision, ct) : null;
        if (candidates is { IsSuccess: true })
            foreach (var candidate in candidates.Value!.Items)
            {
                if (candidate.Decision is not { Decision: "Confirmed" or "Retyped" } decision || candidate.FloorId is null || candidate.Position.Count < 3) continue;
                var key = decision.CatalogKey ?? candidate.CatalogKey;
                if (FireInfrastructureRules.ScenarioItemCapabilities.Contains(key) || key == EditorContract.BuildingOnlyCapability) continue;
                var half = (double)(candidate.RotationY ?? 0) * Math.PI / 360;
                var item = new JsonObject
                {
                    ["id"] = $"infra-eq-{items.Count + 1}",
                    ["kind"] = "Equipment",
                    ["category"] = key.StartsWith("water.", StringComparison.Ordinal) ? "WaterSource" : "Equipment",
                    ["label"] = candidate.IfcClass ?? key,
                    ["placement"] = new JsonObject
                    {
                        ["floorId"] = candidate.FloorId,
                        ["position"] = new JsonObject { ["x"] = candidate.Position[0], ["y"] = candidate.Position[1], ["z"] = candidate.Position[2] },
                        ["rotation"] = new JsonObject { ["x"] = 0, ["y"] = Math.Round(Math.Sin(half), 9), ["z"] = 0, ["w"] = Math.Round(Math.Cos(half), 9) }
                    },
                    ["capability"] = new JsonObject { ["id"] = key, ["version"] = contracts.TryGetValue(key, out var contract) ? contract.Version : "1.0.0" }
                };
                if (EditorContract.IfcGlobalIdPattern().IsMatch(candidate.IfcGlobalId)) item["anchorId"] = candidate.IfcGlobalId;
                items.Add(item);
            }
        return new { runtimeVersion = runtime?.RuntimeVersion, items };
    }

    private static AuthResult<T> Invalid<T>(IReadOnlyList<ScenarioDraftValidationIssue> issues) =>
        new(default, new AuthError("FIRE_INFRASTRUCTURE_INVALID", "Fire infrastructure items are not valid.", 422, Issues: issues));

    private static AuthResult<T> Map<T>(AuthResult<JsonElement> result) =>
        result.IsSuccess ? AuthResult<T>.Ok(result.Value.Deserialize<T>(Json)!) : new(default, result.Error);
}
