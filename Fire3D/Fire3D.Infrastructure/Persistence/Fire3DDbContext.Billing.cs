using Fire3D.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Persistence;

public partial class Fire3DDbContext
{
    private static void ConfigureBilling(ModelBuilder model)
    {
        model.Entity<Quotation>(e =>
        {
            e.Property(x=>x.BillingPurpose).HasColumnName("billing_purpose").HasMaxLength(30).HasDefaultValue("BuildingService");
            e.Property(x=>x.DiscountRuleId).HasColumnName("discount_rule_id");
            e.Property(x=>x.DiscountSnapshot).HasColumnName("discount_snapshot").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x=>x.PriceSnapshot).HasColumnName("price_snapshot").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x=>x.TermsSnapshot).HasColumnName("terms_snapshot").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x=>x.Revision).HasColumnName("revision").HasDefaultValue(1L).IsConcurrencyToken();
            e.HasOne<ServicePackageDiscountRule>().WithMany().HasForeignKey(x=>x.DiscountRuleId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PayosPaymentRequest>().Property(x=>x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(255).HasDefaultValueSql("gen_random_uuid()::text");
        model.Entity<PayosPaymentRequest>().HasIndex(x=>x.IdempotencyKey).IsUnique();
        model.Entity<ServicePackageDiscountRule>().HasIndex(x=>x.Code).IsUnique();
        model.Entity<QuotationBuildingItem>().HasIndex(x=>new {x.QuotationId,x.BuildingId}).IsUnique();
        model.Entity<QuotationBuildingItem>().HasIndex(x=>x.LineProvisioningKey).IsUnique();
        model.Entity<QuotationBuildingItem>().HasOne<Quotation>().WithMany().HasForeignKey(x=>x.QuotationId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<QuotationBuildingItem>().HasOne<Building>().WithMany().HasForeignKey(x=>x.BuildingId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<QuotationBuildingItem>().HasOne<ServicePackage>().WithMany().HasForeignKey(x=>x.ServicePackageId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ServiceEntitlement>().HasIndex(x=>x.ProvisioningKey).IsUnique();
        model.Entity<ServiceEntitlement>().HasIndex(x=>new {x.QuotationItemId,x.PaymentTransactionId}).IsUnique();
        model.Entity<PaymentProvisioningRecord>().HasIndex(x=>new {x.PaymentTransactionId,x.QuotationItemId}).IsUnique();
        model.Entity<OrganizationNotification>().HasIndex(x=>x.IdempotencyKey).IsUnique();
        model.Entity<NotificationDelivery>().HasIndex(x=>new {x.NotificationId,x.Channel}).IsUnique();
        model.Entity<EnterpriseQuoteRequest>().HasIndex(x=>x.IdempotencyKey).IsUnique();
        model.Entity<ServicePackageDiscountRule>();
        model.Entity<ServiceEntitlement>();
        model.Entity<PaymentProvisioningRecord>();
        model.Entity<OrganizationNotification>();
        model.Entity<NotificationDelivery>();
        model.Entity<EnterpriseQuoteRequest>();
        model.Entity<BillingCommandReceipt>().HasIndex(x=>new {x.ActorId,x.Operation,x.IdempotencyKey}).IsUnique();
        model.Entity<BillingCheckoutOperation>().HasIndex(x=>new {x.ActorId,x.IdempotencyKey}).IsUnique();
        model.Entity<BillingCheckoutOperation>().HasIndex(x=>x.OrderCode).IsUnique();
        model.Entity<BillingCheckoutOperation>().HasIndex(x=>x.QuotationId).IsUnique().HasFilter("status IN ('Creating','Ready','NeedsReconcile')");
        model.Entity<PayosWebhookInboxEntry>().HasIndex(x=>x.EventKey).IsUnique();
    }
}
