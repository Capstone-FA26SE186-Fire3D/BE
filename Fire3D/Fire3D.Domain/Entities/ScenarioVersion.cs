using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

/// <summary>
/// Append-only snapshot. Confirmation is per version through revision_reviews, not an exclusive lock on all scenarios of the revision.
/// </summary>
public partial class ScenarioVersion
{
    public Guid Id { get; set; }

    public Guid ScenarioId { get; set; }

    public Guid RevisionId { get; set; }

    public Guid BuildingId { get; set; }

    public Guid OrganizationId { get; set; }

    public int VersionNumber { get; set; }

    public string Name { get; set; } = null!;

    public string SchemaVersion { get; set; } = null!;

    public string AlgorithmVersion { get; set; } = null!;

    public long RandomSeed { get; set; }

    public int TimeLimitSeconds { get; set; }

    public string SpawnConfig { get; set; } = null!;

    public string GoalConfig { get; set; } = null!;

    public string FireSourceConfig { get; set; } = null!;

    public string NpcConfig { get; set; } = null!;

    public string BlockedElements { get; set; } = null!;

    public string RoutingConfig { get; set; } = null!;

    public string ScoringConfig { get; set; } = null!;

    public string ModePolicy { get; set; } = null!;

    public string SafetyThresholds { get; set; } = null!;

    public int ReplanIntervalSeconds { get; set; }

    public string ScenarioHash { get; set; } = null!;

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual ICollection<ProcessingJob> ProcessingJobs { get; set; } = new List<ProcessingJob>();

    public virtual ICollection<Release> Releases { get; set; } = new List<Release>();

    public virtual Revision Revision { get; set; } = null!;

    public virtual RevisionReview? RevisionReview { get; set; }

    public virtual Scenario Scenario { get; set; } = null!;

    public virtual ICollection<ValidationRun> ValidationRuns { get; set; } = new List<ValidationRun>();
}
