using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

/// <summary>
/// Append-only attestation from a trusted validator; SQL checks bindings, not geometry, routing or cryptographic file contents.
/// </summary>
public partial class ValidationRun
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public Guid JobId { get; set; }

    public string Kind { get; set; } = null!;

    public Guid? ScenarioVersionId { get; set; }

    public Guid? CandidateArtifactId { get; set; }

    public Guid? AnnotationSetId { get; set; }

    public string Outcome { get; set; } = null!;

    public string? ScenarioHash { get; set; }

    public string ValidatorVersion { get; set; } = null!;

    public string Report { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual AnnotationSet? AnnotationSet { get; set; }

    public virtual ProcessingJob ProcessingJob { get; set; } = null!;

    public virtual Revision Revision { get; set; } = null!;

    public virtual RevisionArtifact? RevisionArtifact { get; set; }

    public virtual ICollection<RevisionIssue> RevisionIssues { get; set; } = new List<RevisionIssue>();

    public virtual ICollection<RevisionReview> RevisionReviews { get; set; } = new List<RevisionReview>();

    public virtual ScenarioVersion? ScenarioVersion { get; set; }
}
