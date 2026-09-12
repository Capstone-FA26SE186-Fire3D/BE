using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class Revision
{
    public Guid Id { get; set; }

    public Guid BuildingId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid UploadedBy { get; set; }

    public string VersionLabel { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<AnnotationSet> AnnotationSets { get; set; } = new List<AnnotationSet>();

    public virtual Building Building { get; set; } = null!;

    public virtual ProcessingJob? ProcessingJob { get; set; }

    public virtual ICollection<Release> Releases { get; set; } = new List<Release>();

    public virtual ICollection<RevisionArtifact> RevisionArtifacts { get; set; } = new List<RevisionArtifact>();

    public virtual ICollection<RevisionFloor> RevisionFloors { get; set; } = new List<RevisionFloor>();

    public virtual ICollection<RevisionProcessingLog> RevisionProcessingLogs { get; set; } = new List<RevisionProcessingLog>();

    public virtual ICollection<RevisionReview> RevisionReviews { get; set; } = new List<RevisionReview>();

    public virtual ICollection<ScenarioVersion> ScenarioVersions { get; set; } = new List<ScenarioVersion>();

    public virtual SourceDocument? SourceDocument { get; set; }

    public virtual User UploadedByNavigation { get; set; } = null!;

    public virtual ICollection<ValidationRun> ValidationRuns { get; set; } = new List<ValidationRun>();
}
