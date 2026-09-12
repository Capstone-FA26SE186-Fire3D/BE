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

    public bool IsActive { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public virtual BuildingContact? BuildingContact { get; set; }

    public virtual ICollection<BuildingFloor> BuildingFloors { get; set; } = new List<BuildingFloor>();

    public virtual BuildingLocation? BuildingLocation { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual Organization Organization { get; set; } = null!;

    public virtual ICollection<Revision> Revisions { get; set; } = new List<Revision>();

    public virtual ICollection<Scenario> Scenarios { get; set; } = new List<Scenario>();
}
