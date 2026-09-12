using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class Feedback
{
    public Guid Id { get; set; }

    public Guid SubmittedBy { get; set; }

    public Guid? OrganizationId { get; set; }

    public Guid? SessionId { get; set; }

    public string Category { get; set; } = null!;

    public int? Rating { get; set; }

    public string Message { get; set; } = null!;

    public Guid? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Organization? Organization { get; set; }

    public virtual User? ReviewedByNavigation { get; set; }

    public virtual Session? Session { get; set; }

    public virtual User SubmittedByNavigation { get; set; } = null!;

    public virtual ICollection<SupportTicket> SupportTickets { get; set; } = new List<SupportTicket>();
}
