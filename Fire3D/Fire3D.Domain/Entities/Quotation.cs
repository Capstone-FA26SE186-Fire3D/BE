using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class Quotation
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ServicePackageId { get; set; }

    public Guid RequestedBy { get; set; }

    public Guid? IssuedBy { get; set; }

    public string QuotationNumber { get; set; } = null!;

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal SubtotalAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = null!;

    public DateTime ValidUntil { get; set; }

    public DateTime? IssuedAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<InvoiceMetadatum> InvoiceMetadata { get; set; } = new List<InvoiceMetadatum>();

    public virtual User? IssuedByNavigation { get; set; }

    public virtual Organization Organization { get; set; } = null!;

    public virtual ICollection<PayosPaymentRequest> PayosPaymentRequests { get; set; } = new List<PayosPaymentRequest>();

    public virtual User RequestedByNavigation { get; set; } = null!;

    public virtual ServicePackage ServicePackage { get; set; } = null!;
}
