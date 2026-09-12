using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class DebriefArtifact
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public string TrajectoryHeatmap { get; set; } = null!;

    public string OptimalPath { get; set; } = null!;

    public string WrongDecisions { get; set; } = null!;

    public string HazardTimeline { get; set; } = null!;

    public string NpcSummary { get; set; } = null!;

    public bool IsVisibleToTrainee { get; set; }

    public string GeneratorVersion { get; set; } = null!;

    public DateTime GeneratedAt { get; set; }

    public virtual SessionResult Session { get; set; } = null!;
}
