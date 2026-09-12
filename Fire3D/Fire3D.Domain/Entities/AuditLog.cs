using System;
using System.Collections.Generic;
using System.Net;

namespace Fire3D.Domain.Entities;

public partial class AuditLog
{
    public Guid Id { get; set; }

    public Guid? UserId { get; set; }

    public Guid? OrganizationId { get; set; }

    public string ActorType { get; set; } = null!;

    public string TargetEntity { get; set; } = null!;

    public Guid? TargetId { get; set; }

    public Guid CorrelationId { get; set; }

    public string? OldValues { get; set; }

    public string? NewValues { get; set; }

    public IPAddress? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }
}
