using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class InvoiceMetadatum
{
    public Guid Id { get; set; }

    public Guid PaymentTransactionId { get; set; }

    public Guid QuotationId { get; set; }

    public Guid OrganizationId { get; set; }

    public string? InvoiceNumber { get; set; }

    public string LegalName { get; set; } = null!;

    public string? TaxCode { get; set; }

    public string? BillingAddress { get; set; }

    public decimal SubtotalAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = null!;

    public DateTime? IssuedAt { get; set; }

    public string? InvoiceUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Organization Organization { get; set; } = null!;

    public virtual PaymentTransaction PaymentTransaction { get; set; } = null!;

    public virtual Quotation Quotation { get; set; } = null!;
}
