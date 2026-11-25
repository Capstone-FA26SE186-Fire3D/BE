using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;

namespace Fire3D.Application.FireSafety;

/// <summary>
/// "Hồ sơ phương án trực quan" of a Building (BE#215, FR-PLAN-12), in the order of Mẫu PC06: header, I (site layout: exits and
/// assembly points of the locked infrastructure), II (facility, equipment, water sources, contacts), III (hazard: type, risk group,
/// size), IV.1 (situations 1..n) with their in/off-hours coverage, V (infrastructure version history). Read-only; the printed form is a
/// reference, never the signed plan (SignedPlanDocumentId points at that one).
/// </summary>
public sealed record PlanDossierResponse(Guid BuildingId, DateTimeOffset GeneratedAt, Guid? SignedPlanDocumentId, PlanDossierHeader Header,
    PlanDossierSiteLayout? SiteLayout, PlanDossierFacility Facility, PlanDossierHazard Hazard, IReadOnlyList<PlanDossierScenario> Scenarios,
    PlanDossierCoverage Coverage, IReadOnlyList<PlanDossierInfrastructureVersion> History);
public sealed record PlanDossierHeader(string Name, string? Address, string? Phone, int Year);
/// <summary>Mục I: the locked infrastructure the plan uses; Exits and AssemblyPoints are its items of those categories.</summary>
public sealed record PlanDossierSiteLayout(Guid RevisionId, Guid InfrastructureId, int InfrastructureVersion, JsonNode Exits, JsonNode AssemblyPoints);
/// <summary>Mục II: facility information (BE#212), equipment counted by catalog key (II.5) and water sources (II.6) of the infrastructure.</summary>
public sealed record PlanDossierFacility(string Name, string? FacilityHeadName, string? FacilityHeadPhone, OnSiteForce? OnSiteForce,
    IReadOnlyList<EmergencyContact> EmergencyContacts, IReadOnlyList<PlanDossierEquipmentCount> Equipment, JsonNode WaterSources);
public sealed record PlanDossierEquipmentCount(string CatalogKey, int Count);
/// <summary>Mục III: RiskGroup is computed like the Building response (Phụ lục II Nghị định 105/2025/NĐ-CP).</summary>
public sealed record PlanDossierHazard(string? BuildingType, string? RiskGroup, int TotalFloors, decimal? FloorAreaSquareMeters);
/// <summary>Mục IV.1: the newest version of each scenario on the infrastructure, numbered in creation order.</summary>
public sealed record PlanDossierScenario(int Number, Guid ScenarioId, string ScenarioName, Guid ScenarioVersionId, int VersionNumber,
    string? Audience, JsonNode? Assumption, string? WorkingHours, string ReviewStatus, DateTimeOffset? ReviewedAt);
/// <summary>Publishing needs an Approved situation in hours and one off hours (FR-PLAN-07).</summary>
public sealed record PlanDossierCoverage(bool InHours, bool OffHours)
{
    public bool Complete => InHours && OffHours;
}
/// <summary>Mục V: every infrastructure version of the plan's revision, newest first.</summary>
public sealed record PlanDossierInfrastructureVersion(Guid Id, int Version, string Status, DateTimeOffset? SubmittedAt, DateTimeOffset? DecidedAt);

public interface IPlanDossierStore
{
    Task<AuthResult<JsonElement>> GetAsync(Guid actor, Guid? family, Guid building, Guid? infrastructureId, CancellationToken ct);
}

public interface IPlanDossierService
{
    Task<AuthResult<PlanDossierResponse>> GetAsync(Guid actor, Guid? family, Guid building, Guid? infrastructureId, CancellationToken ct);
}

public sealed class PlanDossierService(IPlanDossierStore store) : IPlanDossierService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AuthResult<PlanDossierResponse>> GetAsync(Guid actor, Guid? family, Guid building, Guid? infrastructureId, CancellationToken ct)
    {
        var result = await store.GetAsync(actor, family, building, infrastructureId, ct);
        if (!result.IsSuccess) return new(default, result.Error);
        var dossier = result.Value.Deserialize<PlanDossierResponse>(Json)!;
        return AuthResult<PlanDossierResponse>.Ok(dossier with
        {
            Hazard = dossier.Hazard with { RiskGroup = BuildingRiskGroup.Compute(dossier.Hazard.BuildingType, dossier.Hazard.TotalFloors, dossier.Hazard.FloorAreaSquareMeters) }
        });
    }
}
