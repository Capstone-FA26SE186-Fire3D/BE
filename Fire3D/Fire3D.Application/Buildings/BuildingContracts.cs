using Fire3D.Application.Administration;

namespace Fire3D.Application.Buildings;

public sealed record CreateBuildingContactRequest(string ContactName, string? ContactRole, string? Phone, string? Email, bool IsPrimary);
public sealed record CreateBuildingLocationRequest(string? Address, string? City, string? District, decimal? Latitude, decimal? Longitude, string? Geojson);

/// <summary>buildingType: Apartment or School (BE#212). The PC06 facility fields are optional; riskGroup is computed by the server.</summary>
public sealed record CreateBuildingRequest(
    string Name,
    string? BuildingType,
    int TotalFloors,
    CreateBuildingLocationRequest? Location,
    CreateBuildingContactRequest? Contact,
    Guid? OrganizationId = null,
    decimal? FloorAreaSquareMeters = null,
    string? FacilityHeadName = null,
    string? FacilityHeadPhone = null,
    OnSiteForce? OnSiteForce = null,
    IReadOnlyList<EmergencyContact>? EmergencyContacts = null);

/// <summary>Replaces the editable fields, including the PC06 facility fields (omitted ones are cleared). buildingType must be chosen again
/// for a legacy Building whose type was not Apartment or School.</summary>
public sealed record UpdateBuildingRequest(
    string Name,
    string? BuildingType,
    int TotalFloors,
    CreateBuildingLocationRequest? Location,
    CreateBuildingContactRequest? Contact,
    decimal? FloorAreaSquareMeters = null,
    string? FacilityHeadName = null,
    string? FacilityHeadPhone = null,
    OnSiteForce? OnSiteForce = null,
    IReadOnlyList<EmergencyContact>? EmergencyContacts = null);

public sealed record BuildingContactResponse(Guid Id, string ContactName, string? ContactRole, string? Phone, string? Email, bool IsPrimary);
public sealed record BuildingLocationResponse(Guid Id, string? Address, string? City, string? District, decimal? Latitude, decimal? Longitude, string? Geojson);

public sealed record BuildingResponse(
    Guid Id, 
    string Name, 
    string? BuildingType, 
    int TotalFloors, 
    bool IsActive, 
    Guid OrganizationId,
    DateTime CreatedAt, 
    DateTime UpdatedAt,
    BuildingLocationResponse? Location,
    BuildingContactResponse? Contact,
    string? RiskGroup = null,
    decimal? FloorAreaSquareMeters = null,
    string? FacilityHeadName = null,
    string? FacilityHeadPhone = null,
    OnSiteForce? OnSiteForce = null,
    IReadOnlyList<EmergencyContact>? EmergencyContacts = null,
    Guid? SignedPlanDocumentId = null);

public sealed record BuildingSummaryResponse(
    Guid Id,
    string Name,
    string? BuildingType,
    int TotalFloors,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? RiskGroup = null);

public sealed record BuildingFilter(int Page, int PageSize, string? Search, bool? IsActive);

public sealed record RevisionResponse(
    Guid Id,
    Guid BuildingId,
    string VersionLabel,
    string Status,
    DateTime CreatedAt,
    SourceDocumentResponse? SourceDocument
);

public sealed record SourceDocumentResponse(
    Guid Id,
    string OriginalFilename,
    long FileSizeBytes,
    string QuarantineStatus,
    DateTime CreatedAt
);
