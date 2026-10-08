using System.ComponentModel.DataAnnotations.Schema;
namespace Fire3D.Domain.Entities;

[Table("billing_quota_policy_versions")]
public sealed class BillingQuotaPolicy
{
    public Guid Id { get; set; }
    public string Audience { get; set; } = "organization";
    public string PolicyKind { get; set; } = "quota";
    public string QuotaUnit { get; set; } = "";
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveUntil { get; set; }
    public string Rollover { get; set; } = "None";
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
