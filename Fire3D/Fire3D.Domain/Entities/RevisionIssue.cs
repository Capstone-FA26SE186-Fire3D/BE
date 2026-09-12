using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class RevisionIssue
{
    public Guid Id { get; set; }

    public Guid ValidationRunId { get; set; }

    public string Severity { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string? IfcGuid { get; set; }

    public string Message { get; set; } = null!;

    public string Details { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual ValidationRun ValidationRun { get; set; } = null!;
}
