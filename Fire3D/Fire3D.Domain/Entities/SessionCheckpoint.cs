using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class SessionCheckpoint
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public int SequenceNumber { get; set; }

    public string PlayerTransform { get; set; } = null!;

    public string PlayerStatus { get; set; } = null!;

    public string WorldInteractiveStates { get; set; } = null!;

    public int HazardTimeStep { get; set; }

    public string NpcStates { get; set; } = null!;

    public string ActiveObjectives { get; set; } = null!;

    public string ReleaseHash { get; set; } = null!;

    public string ScenarioHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual Session Session { get; set; } = null!;
}
