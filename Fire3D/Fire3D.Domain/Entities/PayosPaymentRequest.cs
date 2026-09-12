using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class PayosPaymentRequest
{
    public Guid Id { get; set; }

    public Guid QuotationId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid RequestedBy { get; set; }

    public long OrderCode { get; set; }

    public decimal ExpectedAmount { get; set; }

    public string ExpectedCurrency { get; set; } = null!;

    public string CheckoutUrl { get; set; } = null!;

    public string ReturnUrl { get; set; } = null!;

    public string CancelUrl { get; set; } = null!;

    public Guid? PaidTransactionId { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Organization Organization { get; set; } = null!;

    public virtual PaymentTransaction? PaymentTransaction { get; set; }

    public virtual ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();

    public virtual Quotation Quotation { get; set; } = null!;

    public virtual User RequestedByNavigation { get; set; } = null!;
}
