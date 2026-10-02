using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Fire3D.Domain.Entities;

[Table("billing_command_receipts")]
public sealed class BillingCommandReceipt
{
    [Column("id")] public Guid Id { get; set; }
    [Column("actor_id")] public Guid ActorId { get; set; }
    [Column("operation")] [MaxLength(80)] public string Operation { get; set; } = "";
    [Column("idempotency_key")] [MaxLength(128)] public string IdempotencyKey { get; set; } = "";
    [Column("input_hash")] [MaxLength(64)] public string InputHash { get; set; } = "";
    [Column("resource_id")] public Guid ResourceId { get; set; }
    [Column("response",TypeName="jsonb")] public string? Response { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("billing_checkout_operations")]
public sealed class BillingCheckoutOperation
{
    [Column("session_family_id")] public Guid? SessionFamilyId { get; set; }
    [Column("provider_input",TypeName="jsonb")] public string ProviderInput { get; set; } = "{}";
    [Column("payment_link_id")] [MaxLength(100)] public string? PaymentLinkId { get; set; }
    [Column("payment_request_id")] public Guid? PaymentRequestId { get; set; }
    [Column("expires_at")] public DateTime? ExpiresAt { get; set; }
    [Column("attempts")] public int Attempts { get; set; }
    [Column("next_attempt_at")] public DateTime NextAttemptAt { get; set; }
    [Column("last_error")] [MaxLength(80)] public string? LastError { get; set; }
    [Column("cancel_requested")] public bool CancelRequested { get; set; }
    [Column("id")] public Guid Id { get; set; }
    [Column("quotation_id")] public Guid QuotationId { get; set; }
    [Column("actor_id")] public Guid ActorId { get; set; }
    [Column("idempotency_key")] [MaxLength(128)] public string IdempotencyKey { get; set; } = "";
    [Column("input_hash")] [MaxLength(64)] public string InputHash { get; set; } = "";
    [Column("order_code")] public long OrderCode { get; set; }
    [Column("status")] [MaxLength(30)] public string Status { get; set; } = "Creating";
    [Column("lease_token")] public Guid? LeaseToken { get; set; }
    [Column("lease_until")] public DateTime? LeaseUntil { get; set; }
    [Column("provider_result", TypeName="jsonb")] public string? ProviderResult { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("payos_webhook_inbox")]
public sealed class PayosWebhookInboxEntry
{
    [Column("last_error")] [MaxLength(80)] public string? LastError { get; set; }
    [Column("processed_at")] public DateTime? ProcessedAt { get; set; }
    [Column("id")] public Guid Id { get; set; }
    [Column("event_key")] [MaxLength(255)] public string EventKey { get; set; } = "";
    [Column("input_hash")] [MaxLength(64)] public string InputHash { get; set; } = "";
    [Column("order_code")] public long OrderCode { get; set; }
    [Column("payload", TypeName="jsonb")] public string Payload { get; set; } = "{}";
    [Column("verified_at")] public DateTime VerifiedAt { get; set; }
    [Column("status")] [MaxLength(30)] public string Status { get; set; } = "Pending";
    [Column("attempts")] public int Attempts { get; set; }
    [Column("lease_token")] public Guid? LeaseToken { get; set; }
    [Column("lease_until")] public DateTime? LeaseUntil { get; set; }
    [Column("next_attempt_at")] public DateTime NextAttemptAt { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}
