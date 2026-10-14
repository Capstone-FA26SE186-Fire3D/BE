using System.Text.Json;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Administration;

public sealed class PlatformServiceAdmin(Fire3DDbContext db) : IPlatformServiceAdmin
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AuthResult<IReadOnlyList<RuntimeCatalogEntry>>> ListRuntimesAsync(Guid actor, Guid family, CancellationToken ct) =>
        Map<IReadOnlyList<RuntimeCatalogEntry>>(await Gate("runtime_catalog_gate", "List", actor, family, Guid.Empty, new { }, ct));

    public async Task<AuthResult<RuntimeCatalogEntry>> CreateRuntimeAsync(Guid actor, Guid family, CreateRuntimeCatalogRequest request, CancellationToken ct)
    {
        // Same contract reader the editor uses, so a published catalog row is always usable by authoring.
        var issues = new List<ScenarioDraftValidationIssue>();
        var capabilities = new System.Text.Json.Nodes.JsonArray((request.Capabilities ?? []).Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray());
        CapabilityContracts.Parse(capabilities, request.CapabilityContracts, issues);
        if (issues.Count > 0)
            return AuthResult<RuntimeCatalogEntry>.Fail("VALIDATION_ERROR", "Capability contracts are invalid.", 400,
                issues.GroupBy(x => x.Path).ToDictionary(x => x.Key, x => x.Select(i => i.Message).ToArray()));
        return Map<RuntimeCatalogEntry>(await Gate("runtime_catalog_gate", "Create", actor, family, Guid.Empty, new
        {
            runtimeVersion = request.RuntimeVersion, protocolVersion = request.ProtocolVersion, manifestSchemaVersion = request.ManifestSchemaVersion,
            capabilities = request.Capabilities ?? [], capabilityContracts = request.CapabilityContracts, isActive = request.IsActive
        }, ct));
    }

    public async Task<AuthResult<RuntimeCatalogEntry>> SetRuntimeActiveAsync(Guid actor, Guid family, Guid id, bool isActive, CancellationToken ct) =>
        Map<RuntimeCatalogEntry>(await Gate("runtime_catalog_gate", "SetActive", actor, family, id, new { isActive }, ct));

    public async Task<AuthResult<IReadOnlyList<TrialEntitlementView>>> ListTrialsAsync(Guid actor, Guid family, Guid building, CancellationToken ct) =>
        Map<IReadOnlyList<TrialEntitlementView>>(await Gate("trial_entitlement_gate", "List", actor, family, building, new { }, ct));

    public async Task<AuthResult<TrialEntitlementView>> CreateTrialAsync(Guid actor, Guid family, Guid building, CreateTrialRequest request, CancellationToken ct) =>
        Map<TrialEntitlementView>(await Gate("trial_entitlement_gate", "Create", actor, family, building,
            new { servicePackageId = request.ServicePackageId, days = request.Days, playtestUnits = request.PlaytestUnits }, ct));

    public async Task<AuthResult<TrialEntitlementView>> UpdateTrialAsync(Guid actor, Guid family, Guid building, Guid entitlement, UpdateTrialRequest request, CancellationToken ct) =>
        Map<TrialEntitlementView>(await Gate("trial_entitlement_gate", "Update", actor, family, building,
            new { entitlementId = entitlement, playtestUnits = request.PlaytestUnits, cancel = request.Cancel }, ct));

    private static AuthResult<T> Map<T>(AuthResult<JsonElement> result) =>
        result.IsSuccess ? AuthResult<T>.Ok(result.Value.Deserialize<T>(Json)!) : new(default, result.Error);

    private async Task<AuthResult<JsonElement>> Gate(string function, string action, Guid actor, Guid family, Guid resource, object input, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT {function}(@action,@actor,@family,@resource,@input)::text", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("family", family);
        command.Parameters.AddWithValue("resource", resource);
        command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(input, Json));
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        var value = document.RootElement;
        var code = value.GetProperty("code").GetString()!;
        if (code == "OK") return AuthResult<JsonElement>.Ok(value.GetProperty("result").Clone());
        return AuthResult<JsonElement>.Fail(code, code switch
        {
            "RUNTIME_VERSION_EXISTS" => "This runtime version is already registered; versions are immutable.",
            "BUILDING_SERVICE_ACTIVE" => "The Building already has a paid service period running.",
            "TRIAL_ALREADY_ACTIVE" => "The Building already has a Trial that has not ended; adjust or cancel it instead.",
            "TRIAL_UNITS_BELOW_USED" => "Playtest units cannot go below the units already used (maximum 1000).",
            "SERVICE_PACKAGE_NOT_FOUND" => "The service package does not exist.",
            "NOT_FOUND" => "Not found.",
            "FORBIDDEN" => "Only PlatformAdmin can manage runtimes and Trials.",
            "UNAUTHORIZED" => "The session is no longer active.",
            _ => "The request is invalid."
        }, value.GetProperty("status").GetInt32());
    }
}
