using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class Organization
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public string Plan { get; set; } = null!;

    public bool IsActive { get; set; }

    public string Metadata { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public virtual ICollection<Building> Buildings { get; set; } = new List<Building>();

    public virtual ICollection<Feedback> Feedbacks { get; set; } = new List<Feedback>();

    public virtual ICollection<InvoiceMetadatum> InvoiceMetadata { get; set; } = new List<InvoiceMetadatum>();

    public virtual ICollection<PayosPaymentRequest> PayosPaymentRequests { get; set; } = new List<PayosPaymentRequest>();

    public virtual ICollection<Quotation> Quotations { get; set; } = new List<Quotation>();

    public virtual ICollection<SupportTicket> SupportTickets { get; set; } = new List<SupportTicket>();

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
