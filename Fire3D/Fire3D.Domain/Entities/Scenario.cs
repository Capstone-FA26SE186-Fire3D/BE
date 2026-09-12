using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

/// <summary>
/// Logical training scenario belonging to a building; versions may pin different compatible building revisions.
/// </summary>
public partial class Scenario
{
    public Guid Id { get; set; }

    public Guid BuildingId { get; set; }

    public Guid OrganizationId { get; set; }

    public string Name { get; set; } = null!;

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Building Building { get; set; } = null!;

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ICollection<ScenarioVersion> ScenarioVersions { get; set; } = new List<ScenarioVersion>();
}
