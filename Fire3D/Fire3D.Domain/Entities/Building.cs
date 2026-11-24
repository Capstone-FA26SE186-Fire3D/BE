using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class Building
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string Name { get; set; } = null!;

    public string? BuildingType { get; set; }

    public int TotalFloors { get; set; }

    /// <summary>BE#212: tổng diện tích sàn (m²), facility head, on-site force (jsonb), emergency contacts (jsonb array), signed PC06 plan.</summary>
    public decimal? FloorAreaSquareMeters { get; set; }

    public string? FacilityHeadName { get; set; }

    public string? FacilityHeadPhone { get; set; }

    public string? OnSiteForce { get; set; }

    public string EmergencyContacts { get; set; } = "[]";

    public Guid? SignedPlanDocumentId { get; set; }

    public bool IsActive { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public virtual BuildingContact? BuildingContact { get; set; }

    public virtual BuildingLocation? BuildingLocation { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual Organization Organization { get; set; } = null!;

    public virtual ICollection<Revision> Revisions { get; set; } = new List<Revision>();

    public virtual ICollection<Scenario> Scenarios { get; set; } = new List<Scenario>();
}
