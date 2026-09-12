using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class ReleasePackage
{
    public Guid Id { get; set; }

    public Guid ReleaseId { get; set; }

    public Guid CandidateArtifactId { get; set; }

    public string ManifestUrl { get; set; } = null!;

    public string ManifestSha256 { get; set; } = null!;

    public string PackageUrl { get; set; } = null!;

    public string ChecksumSha256 { get; set; } = null!;

    public long PackageSizeBytes { get; set; }

    public string MinRuntimeVersion { get; set; } = null!;

    public string SchemaVersion { get; set; } = null!;

    public string BuildTarget { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual RevisionArtifact CandidateArtifact { get; set; } = null!;

    public virtual Release Release { get; set; } = null!;
}
