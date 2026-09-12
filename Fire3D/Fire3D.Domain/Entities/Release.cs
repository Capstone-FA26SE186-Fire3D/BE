using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class Release
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public Guid ScenarioVersionId { get; set; }

    public Guid BuildingId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ConfirmationReviewId { get; set; }

    public Guid? PublishedBy { get; set; }

    public Guid? RevokedBy { get; set; }

    public string SafetyThresholds { get; set; } = null!;

    public string? RevokedReason { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual RevisionReview ConfirmationReview { get; set; } = null!;

    public virtual User? PublishedByNavigation { get; set; }

    public virtual ReleasePackage? ReleasePackage { get; set; }

    public virtual Revision Revision { get; set; } = null!;

    public virtual User? RevokedByNavigation { get; set; }

    public virtual ScenarioVersion ScenarioVersion { get; set; } = null!;

    public virtual ICollection<Training> Training { get; set; } = new List<Training>();
}
