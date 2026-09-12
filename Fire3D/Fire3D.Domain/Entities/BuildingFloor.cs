using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class BuildingFloor
{
    public Guid Id { get; set; }

    public Guid BuildingId { get; set; }

    public Guid OrganizationId { get; set; }

    public int FloorNumber { get; set; }

    public string? FloorName { get; set; }

    public string? FloorPlanUrl { get; set; }

    public decimal? AreaSqm { get; set; }

    public decimal? ElevationMeters { get; set; }

    public bool IsBasement { get; set; }

    public string Metadata { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Building Building { get; set; } = null!;

    public virtual ICollection<RevisionFloor> RevisionFloors { get; set; } = new List<RevisionFloor>();
}
