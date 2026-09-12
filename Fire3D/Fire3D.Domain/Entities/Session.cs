using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class Session
{
    public Guid Id { get; set; }

    public Guid TrainingId { get; set; }

    public Guid ReleaseId { get; set; }

    public Guid ScenarioVersionId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid TraineeUserId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid QrCodeId { get; set; }

    public Guid StartKey { get; set; }

    public string AppVersion { get; set; } = null!;

    public string UnityVersion { get; set; } = null!;

    public string ProtocolVersion { get; set; } = null!;

    public string ScenarioHash { get; set; } = null!;

    public string ReleaseHash { get; set; } = null!;

    public DateTime StartedAt { get; set; }

    public DateTime? LaunchedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public string? TerminalReason { get; set; }

    public virtual ICollection<Feedback> Feedbacks { get; set; } = new List<Feedback>();

    public virtual ReleaseQrCode ReleaseQrCode { get; set; } = null!;

    public virtual ICollection<SessionCheckpoint> SessionCheckpoints { get; set; } = new List<SessionCheckpoint>();

    public virtual ICollection<SessionEvent> SessionEvents { get; set; } = new List<SessionEvent>();

    public virtual SessionResult? SessionResult { get; set; }

    public virtual ICollection<SupportTicket> SupportTickets { get; set; } = new List<SupportTicket>();

    public virtual User TraineeUser { get; set; } = null!;

    public virtual Training Training { get; set; } = null!;

    public virtual UserDevice UserDevice { get; set; } = null!;
}
