using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Fire3D.Domain.Entities;

[Table("organization_notifications")]
public sealed class OrganizationNotification
{
    [Column("id")]
    public Guid Id { get; set; }
    [Column("organization_id")]
    public Guid OrganizationId { get; set; }
    [Column("recipient_user_id")]
    public Guid RecipientUserId { get; set; }
    [Column("building_id")]
    public Guid BuildingId { get; set; }
    [Column("entitlement_id")]
    public Guid EntitlementId { get; set; }
    [Column("notification_type")]
    [MaxLength(50)]
    public string NotificationType { get; set; } = "";
    [Column("title")]
    public string Title { get; set; } = "";
    [Column("body")]
    public string Body { get; set; } = "";
    [Column("reference_ends_at")]
    public DateTime ReferenceEndsAt { get; set; }
    [Column("idempotency_key")]
    [MaxLength(255)]
    public string IdempotencyKey { get; set; } = "";
    [Column("read_at")]
    public DateTime? ReadAt { get; set; }
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}

[Table("enterprise_quote_requests")]
public sealed class EnterpriseQuoteRequest
{
    [Column("id")]
    public Guid Id { get; set; }
    [Column("organization_id")]
    public Guid OrganizationId { get; set; }
    [Column("requested_by")]
    public Guid RequestedBy { get; set; }
    [Column("requested_building_count")]
    public int RequestedBuildingCount { get; set; }
    [Column("requested_duration_months")]
    public int? RequestedDurationMonths { get; set; }
    [Column("contact_name")]
    public string ContactName { get; set; } = "";
    [Column("contact_email")]
    [MaxLength(255)]
    public string ContactEmail { get; set; } = "";
    [Column("contact_phone")]
    [MaxLength(50)]
    public string? ContactPhone { get; set; }
    [Column("notes")]
    public string? Notes { get; set; }
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "";
    [Column("quotation_id")]
    public Guid? QuotationId { get; set; }
    [Column("idempotency_key")]
    [MaxLength(255)]
    public string IdempotencyKey { get; set; } = "";
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

[Table("payment_provisioning_records")]
public sealed class PaymentProvisioningRecord
{
    [Column("id")]
    public Guid Id { get; set; }
    [Column("payment_transaction_id")]
    public Guid PaymentTransactionId { get; set; }
    [Column("quotation_id")]
    public Guid QuotationId { get; set; }
    [Column("quotation_item_id")]
    public Guid QuotationItemId { get; set; }
    [Column("organization_id")]
    public Guid OrganizationId { get; set; }
    [Column("provisioning_key")]
    [MaxLength(255)]
    public string ProvisioningKey { get; set; } = "";
    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "";
    [Column("attempts")]
    public int Attempts { get; set; }
    [Column("last_error")]
    public string? LastError { get; set; }
    [Column("provisioned_at")]
    public DateTime? ProvisionedAt { get; set; }
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

[Table("quotation_building_items")]
public sealed class QuotationBuildingItem
{
    [Column("id")]
    public Guid Id { get; set; }
    [Column("quotation_id")]
    public Guid QuotationId { get; set; }
    [Column("building_id")]
    public Guid BuildingId { get; set; }
    [Column("service_package_id")]
    public Guid ServicePackageId { get; set; }
    [Column("purchase_action")]
    [MaxLength(20)]
    public string PurchaseAction { get; set; } = "";
    [Column("service_duration_months")]
    public int ServiceDurationMonths { get; set; }
    [Column("unit_price", TypeName = "numeric(14,2)")]
    public decimal UnitPrice { get; set; }
    [Column("discount_amount", TypeName = "numeric(14,2)")]
    public decimal DiscountAmount { get; set; }
    [Column("subtotal_amount", TypeName = "numeric(14,2)")]
    public decimal SubtotalAmount { get; set; }
    [Column("total_amount", TypeName = "numeric(14,2)")]
    public decimal TotalAmount { get; set; }
    [Column("currency")]
    [MaxLength(3)]
    public string Currency { get; set; } = "";
    [Column("price_snapshot", TypeName = "jsonb")]
    public string PriceSnapshot { get; set; } = "";
    [Column("terms_snapshot", TypeName = "jsonb")]
    public string TermsSnapshot { get; set; } = "";
    [Column("discount_snapshot", TypeName = "jsonb")]
    public string DiscountSnapshot { get; set; } = "";
    [Column("line_provisioning_key")]
    [MaxLength(255)]
    public string? LineProvisioningKey { get; set; }
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

[Table("service_package_discount_rules")]
public sealed class ServicePackageDiscountRule
{
    [Column("id")]
    public Guid Id { get; set; }
    [Column("service_package_id")]
    public Guid? ServicePackageId { get; set; }
    [Column("code")]
    [MaxLength(80)]
    public string Code { get; set; } = "";
    [Column("discount_kind")]
    [MaxLength(20)]
    public string DiscountKind { get; set; } = "";
    [Column("discount_value", TypeName = "numeric(14,2)")]
    public decimal DiscountValue { get; set; }
    [Column("discount_currency")]
    [MaxLength(3)]
    public string? DiscountCurrency { get; set; }
    [Column("minimum_buildings")]
    public int MinimumBuildings { get; set; }
    [Column("minimum_duration_months")]
    public int? MinimumDurationMonths { get; set; }
    [Column("valid_from")]
    public DateTime ValidFrom { get; set; }
    [Column("valid_until")]
    public DateTime? ValidUntil { get; set; }
    [Column("is_active")]
    public bool IsActive { get; set; }
    [Column("created_by")]
    public Guid CreatedBy { get; set; }
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

[Table("notification_deliveries")]
public sealed class NotificationDelivery
{
    [Column("id")]
    public Guid Id { get; set; }
    [Column("notification_id")]
    public Guid NotificationId { get; set; }
    [Column("channel")]
    [MaxLength(20)]
    public string Channel { get; set; } = "";
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "";
    [Column("attempts")]
    public int Attempts { get; set; }
    [Column("provider_message_id")]
    public string? ProviderMessageId { get; set; }
    [Column("last_error")]
    public string? LastError { get; set; }
    [Column("sent_at")]
    public DateTime? SentAt { get; set; }
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

[Table("service_entitlements")]
public sealed class ServiceEntitlement
{
    [Column("id")]
    public Guid Id { get; set; }
    [Column("organization_id")]
    public Guid OrganizationId { get; set; }
    [Column("building_id")]
    public Guid BuildingId { get; set; }
    [Column("service_package_id")]
    public Guid ServicePackageId { get; set; }
    [Column("quotation_id")]
    public Guid? QuotationId { get; set; }
    [Column("quotation_item_id")]
    public Guid? QuotationItemId { get; set; }
    [Column("payment_transaction_id")]
    public Guid? PaymentTransactionId { get; set; }
    [Column("provisioning_key")]
    [MaxLength(255)]
    public string ProvisioningKey { get; set; } = "";
    [Column("status")]
    [MaxLength(30)]
    public string Status { get; set; } = "";
    [Column("starts_at")]
    public DateTime StartsAt { get; set; }
    [Column("ends_at")]
    public DateTime EndsAt { get; set; }
    [Column("price_snapshot", TypeName = "jsonb")]
    public string PriceSnapshot { get; set; } = "";
    [Column("terms_snapshot", TypeName = "jsonb")]
    public string TermsSnapshot { get; set; } = "";
    [Column("playtest_units_granted")]
    public int PlaytestUnitsGranted { get; set; }
    [Column("playtest_units_used")]
    public int PlaytestUnitsUsed { get; set; }
    [Column("created_by")]
    public Guid CreatedBy { get; set; }
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
