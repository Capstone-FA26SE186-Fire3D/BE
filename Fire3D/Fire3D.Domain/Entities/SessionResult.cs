using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

/// <summary>
/// One immutable server-accepted result per session. Backend validates/calculates rubric before insert; no client score trust implied.
/// </summary>
public partial class SessionResult
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public Guid CompletionKey { get; set; }

    public long LastEventSequence { get; set; }

    public string SubmissionPayload { get; set; } = null!;

    public string ResultSchemaVersion { get; set; } = null!;

    public string RubricVersion { get; set; } = null!;

    public decimal Score { get; set; }

    public int TimeTakenSeconds { get; set; }

    public int WrongExits { get; set; }

    public decimal HazardExposureScore { get; set; }

    public decimal TotalDistanceMeters { get; set; }

    public bool ReachedExit { get; set; }

    public string? ExitPointId { get; set; }

    public string PathTraveled { get; set; } = null!;

    public DateTime? ClientStartedAt { get; set; }

    public DateTime? ClientEndedAt { get; set; }

    public DateTime SyncedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual DebriefArtifact? DebriefArtifact { get; set; }

    public virtual Session Session { get; set; } = null!;
}
