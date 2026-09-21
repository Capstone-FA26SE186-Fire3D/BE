using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class ProcessingJob
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public Guid SourceDocumentId { get; set; }

    public string Kind { get; set; } = null!;

    public Guid? ScenarioVersionId { get; set; }

    public Guid JobKey { get; set; }

    public string Status { get; set; } = null!;

    

    public string InputHash { get; set; } = null!;

    public string? LeaseOwner { get; set; }

    public DateTime? HeartbeatAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Revision Revision { get; set; } = null!;

    public virtual ICollection<RevisionArtifact> RevisionArtifacts { get; set; } = new List<RevisionArtifact>();

    public virtual ICollection<RevisionProcessingLog> RevisionProcessingLogs { get; set; } = new List<RevisionProcessingLog>();

    public virtual ScenarioVersion? ScenarioVersion { get; set; }

    public virtual SourceDocument SourceDocument { get; set; } = null!;

    public virtual ICollection<ValidationRun> ValidationRuns { get; set; } = new List<ValidationRun>();
}

