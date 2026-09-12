using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class SupportTicket
{
    public Guid Id { get; set; }

    public string TicketNumber { get; set; } = null!;

    public Guid CreatedBy { get; set; }

    public Guid? OrganizationId { get; set; }

    public Guid? SessionId { get; set; }

    public Guid? FeedbackId { get; set; }

    public Guid? AssignedTo { get; set; }

    public string Subject { get; set; } = null!;

    public string Description { get; set; } = null!;

    public DateTime? ResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual User? AssignedToNavigation { get; set; }

    public virtual User CreatedByNavigation { get; set; } = null!;

    public virtual Feedback? Feedback { get; set; }

    public virtual Organization? Organization { get; set; }

    public virtual Session? Session { get; set; }
}
