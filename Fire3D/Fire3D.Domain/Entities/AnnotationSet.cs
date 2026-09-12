using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class AnnotationSet
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public int VersionNumber { get; set; }

    public string Data { get; set; } = null!;

    public string Provenance { get; set; } = null!;

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual Revision Revision { get; set; } = null!;

    public virtual ICollection<RevisionReview> RevisionReviews { get; set; } = new List<RevisionReview>();

    public virtual ICollection<ValidationRun> ValidationRuns { get; set; } = new List<ValidationRun>();
}
