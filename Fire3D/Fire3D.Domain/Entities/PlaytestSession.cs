using System;

namespace Fire3D.Domain.Entities;

public partial class PlaytestSession
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid BuildingId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? ScenarioDraftId { get; set; }
    public Guid ScenarioVersionId { get; set; }
    public Guid ServiceEntitlementId { get; set; }
    public Guid CreatedBy { get; set; }
    public string PackageHash { get; set; } = null!;
    public string ProtocolVersion { get; set; } = null!;
    public string ManifestSchemaVersion { get; set; } = null!;
    public string? PrepareIdempotencyKey { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? StartIdempotencyKey { get; set; }
    public string Status { get; set; } = null!;
    public string? CompletionIdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public virtual Organization Organization { get; set; } = null!;
    public virtual Building Building { get; set; } = null!;
    public virtual Revision Revision { get; set; } = null!;
    public virtual ScenarioDraft? ScenarioDraft { get; set; }
    public virtual ScenarioVersion ScenarioVersion { get; set; } = null!;
    public virtual User CreatedByNavigation { get; set; } = null!;
}
