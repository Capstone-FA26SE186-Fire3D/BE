using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class Training
{
    public Guid Id { get; set; }

    public Guid ReleaseId { get; set; }

    public Guid ScenarioVersionId { get; set; }

    public Guid OrganizationId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public List<string> AllowedModes { get; set; } = null!;

    /// <summary>
    /// NULL = unlimited. Positive value limits Assessment session creation per Trainee and Training, including launch failures; Learn/Guided unlimited.
    /// </summary>
    public int? MaxAttempts { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual Release Release { get; set; } = null!;

    public virtual ICollection<ReleaseQrCode> ReleaseQrCodes { get; set; } = new List<ReleaseQrCode>();

    public virtual ICollection<Session> Sessions { get; set; } = new List<Session>();
}
