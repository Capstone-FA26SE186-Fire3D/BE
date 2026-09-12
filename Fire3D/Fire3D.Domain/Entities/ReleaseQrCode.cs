using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class ReleaseQrCode
{
    public Guid Id { get; set; }

    public Guid ReleaseId { get; set; }

    public Guid TrainingId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>
    /// Placement metadata only; does not override the immutable scenario spawn.
    /// </summary>
    public Guid? RevisionFloorId { get; set; }

    public Guid CreatedBy { get; set; }

    /// <summary>
    /// SHA-256 of a high-entropy opaque token. Original token returned once for printing; rotation creates a new row.
    /// </summary>
    public string QrHash { get; set; } = null!;

    public string? Label { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual RevisionFloor? RevisionFloor { get; set; }

    public virtual ICollection<Session> Sessions { get; set; } = new List<Session>();

    public virtual Training Training { get; set; } = null!;
}
