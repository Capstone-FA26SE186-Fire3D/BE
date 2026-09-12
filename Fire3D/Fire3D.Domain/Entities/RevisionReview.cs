using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class RevisionReview
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public Guid ScenarioVersionId { get; set; }

    public Guid ReviewedBy { get; set; }

    public Guid ValidationRunId { get; set; }

    public Guid? AnnotationSetId { get; set; }

    public string? ReviewMessage { get; set; }

    public DateTime ReviewedAt { get; set; }

    public virtual AnnotationSet? AnnotationSet { get; set; }

    public virtual ICollection<Release> Releases { get; set; } = new List<Release>();

    public virtual User ReviewedByNavigation { get; set; } = null!;

    public virtual Revision Revision { get; set; } = null!;

    public virtual ScenarioVersion ScenarioVersion { get; set; } = null!;

    public virtual ValidationRun ValidationRun { get; set; } = null!;
}
