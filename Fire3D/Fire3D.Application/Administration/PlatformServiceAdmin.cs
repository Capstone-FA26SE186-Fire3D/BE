using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;

namespace Fire3D.Application.Administration;

/// <summary>Runtime version as registered for package compatibility. Rows are immutable except IsActive: create a new version to change capabilities.</summary>
public sealed record RuntimeCatalogEntry(Guid Id, string RuntimeVersion, string ProtocolVersion, string ManifestSchemaVersion,
    IReadOnlyList<string> Capabilities, JsonElement CapabilityContracts, bool IsActive, DateTime CreatedAt);
/// <summary>CapabilityContracts maps capability id → {version, objectKinds, parameters, description}; every key must be listed in Capabilities.</summary>
public sealed record CreateRuntimeCatalogRequest(string RuntimeVersion, string ProtocolVersion, string ManifestSchemaVersion,
    IReadOnlyList<string> Capabilities, JsonObject? CapabilityContracts = null, bool IsActive = true);
public sealed record SetRuntimeCatalogActiveRequest(bool IsActive);

/// <summary>Trial service of one Building. PlaytestUnitsUsed only grows through playtest starts; IsEffective is true inside the window.</summary>
public sealed record TrialEntitlementView(Guid Id, Guid BuildingId, Guid OrganizationId, Guid ServicePackageId, string Status, DateTime StartsAt, DateTime EndsAt,
    int PlaytestUnitsGranted, int PlaytestUnitsUsed, bool IsEffective, DateTime CreatedAt);
/// <summary>Grants a Trial starting now for Days (1-90) with PlaytestUnits (0-1000). ServicePackageId is the package the Trial previews.</summary>
public sealed record CreateTrialRequest(Guid ServicePackageId, int Days, int PlaytestUnits);
/// <summary>Adjusts a Trial: PlaytestUnits never below the units already used, or Cancel. Dates are immutable; extend by cancelling and granting again.</summary>
public sealed record UpdateTrialRequest(int? PlaytestUnits = null, bool Cancel = false);

public interface IPlatformServiceAdmin
{
    Task<AuthResult<IReadOnlyList<RuntimeCatalogEntry>>> ListRuntimesAsync(Guid actor, Guid family, CancellationToken ct);
    Task<AuthResult<RuntimeCatalogEntry>> CreateRuntimeAsync(Guid actor, Guid family, CreateRuntimeCatalogRequest request, CancellationToken ct);
    Task<AuthResult<RuntimeCatalogEntry>> SetRuntimeActiveAsync(Guid actor, Guid family, Guid id, bool isActive, CancellationToken ct);
    Task<AuthResult<IReadOnlyList<TrialEntitlementView>>> ListTrialsAsync(Guid actor, Guid family, Guid building, CancellationToken ct);
    Task<AuthResult<TrialEntitlementView>> CreateTrialAsync(Guid actor, Guid family, Guid building, CreateTrialRequest request, CancellationToken ct);
    Task<AuthResult<TrialEntitlementView>> UpdateTrialAsync(Guid actor, Guid family, Guid building, Guid entitlement, UpdateTrialRequest request, CancellationToken ct);
}
