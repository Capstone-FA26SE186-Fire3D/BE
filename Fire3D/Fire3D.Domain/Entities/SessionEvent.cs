using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class SessionEvent
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public long SequenceNumber { get; set; }

    public Guid ClientEventId { get; set; }

    public string SchemaVersion { get; set; } = null!;

    public string EventType { get; set; } = null!;

    public string EventData { get; set; } = null!;

    public long ElapsedMs { get; set; }

    public DateTime RecordedAt { get; set; }

    public DateTime ReceivedAt { get; set; }

    public virtual Session Session { get; set; } = null!;
}
