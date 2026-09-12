using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

/// <summary>
/// Immutable floor snapshot used by content, QR labels and historical replay; changes require a new revision.
/// </summary>
public partial class RevisionFloor
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public Guid BuildingId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid BuildingFloorId { get; set; }

    public string IfcGuid { get; set; } = null!;

    public int FloorNumber { get; set; }

    public string FloorName { get; set; } = null!;

    public decimal ElevationMeters { get; set; }

    public string CoordinateTransform { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual BuildingFloor BuildingFloor { get; set; } = null!;

    public virtual ICollection<ReleaseQrCode> ReleaseQrCodes { get; set; } = new List<ReleaseQrCode>();

    public virtual Revision Revision { get; set; } = null!;
}
