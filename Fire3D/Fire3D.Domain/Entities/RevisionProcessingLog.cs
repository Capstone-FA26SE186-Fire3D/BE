using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class RevisionProcessingLog
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }

    public Guid JobId { get; set; }

    public string? Message { get; set; }

    public int? DurationMs { get; set; }

    public int AttemptNumber { get; set; }

    public DateTime LoggedAt { get; set; }

    public virtual ProcessingJob ProcessingJob { get; set; } = null!;

    public virtual Revision Revision { get; set; } = null!;
}
