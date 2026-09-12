using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class RevisionArtifact
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public Guid JobId { get; set; }

    public string ArtifactType { get; set; } = null!;

    public string ObjectKey { get; set; } = null!;

    public string Sha256Hash { get; set; } = null!;

    public long SizeBytes { get; set; }

    public string SchemaVersion { get; set; } = null!;

    public string Metadata { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual ProcessingJob ProcessingJob { get; set; } = null!;

    public virtual ICollection<ReleasePackage> ReleasePackages { get; set; } = new List<ReleasePackage>();

    public virtual Revision Revision { get; set; } = null!;

    public virtual ICollection<ValidationRun> ValidationRuns { get; set; } = new List<ValidationRun>();
}
