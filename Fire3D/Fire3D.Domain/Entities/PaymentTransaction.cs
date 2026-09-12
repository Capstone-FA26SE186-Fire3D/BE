using System;
using System.Collections.Generic;

namespace Fire3D.Domain.Entities;

public partial class PaymentTransaction
{
    public Guid Id { get; set; }

    public Guid PaymentRequestId { get; set; }

    public string WebhookEventId { get; set; } = null!;

    public string? ProviderTransactionId { get; set; }

    public long ReceivedOrderCode { get; set; }

    public decimal ReceivedAmount { get; set; }

    public string ReceivedCurrency { get; set; } = null!;

    public bool SignatureVerified { get; set; }

    public string RawPayload { get; set; } = null!;

    public string? RejectionReason { get; set; }

    public DateTime ReceivedAt { get; set; }

    public DateTime? SignatureVerifiedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public virtual InvoiceMetadatum? InvoiceMetadatum { get; set; }

    public virtual PayosPaymentRequest PaymentRequest { get; set; } = null!;

    public virtual ICollection<PayosPaymentRequest> PayosPaymentRequests { get; set; } = new List<PayosPaymentRequest>();
}
