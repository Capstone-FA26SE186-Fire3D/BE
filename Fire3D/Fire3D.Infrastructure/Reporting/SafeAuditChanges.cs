using System.Globalization;
using System.Text.Json;
using Fire3D.Application.Reporting;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
namespace Fire3D.Infrastructure.Reporting;
public static class SafeAuditChanges
{
    private static readonly string[] Financial=["revision","commercialVersion","unitPrice","subtotalAmount","discountAmount","taxAmount","totalAmount","durationMonths","learnerLimit","aiQuotaUnits","aiPolicyVersionId","packageRevision","startsAt","endsAt","servicePackageId"];
    public static IReadOnlyList<AuditFieldChange> Read(AuditLog audit)
    {
        var fields=audit.TargetEntity.ToLowerInvariant() switch
        {
            "release" when audit.Action is AuditAction.Publish or AuditAction.Revoke or AuditAction.Update => new[]{"status","publishedAt","publishedBy","revokedAt","revokedBy"},
            "service_packages" or "quotations" when audit.Action is AuditAction.Create or AuditAction.Update => Financial.Concat(["status","isActive"]).ToArray(),
            "service_entitlements" when audit.Action is AuditAction.Payment or AuditAction.Grant => new[]{"commercialVersion","learnerLimit","startsAt","endsAt","buildingId","servicePackageId"},
            "billing_ai_quota_grants" when audit.Action==AuditAction.Grant => new[]{"quotaUnits","quotaUnit","startsAt","endsAt","policyVersionId","rollover"},
            "supportticket" or "support_tickets" when audit.Action is AuditAction.Update or AuditAction.Support => new[]{"status","priority","assignedTo","revision","resolvedAt"},
            "scenario_content_reviews" when audit.Action is AuditAction.Create or AuditAction.Update or AuditAction.Reject => new[]{"status","reviewedBy","reviewedAt"},
            _ => Array.Empty<string>()
        };
        if(fields.Length==0 || audit.OldValues?.Length>65536 || audit.NewValues?.Length>65536)return [];
        try
        {
            using var before=JsonDocument.Parse(audit.OldValues??"{}");using var after=JsonDocument.Parse(audit.NewValues??"{}");
            if(before.RootElement.ValueKind!=JsonValueKind.Object || after.RootElement.ValueKind!=JsonValueKind.Object)return [];
            var changes=new List<AuditFieldChange>();
            foreach(var field in fields)
            {
                var old=ReadField(before.RootElement,field,out var oldFound);var current=ReadField(after.RootElement,field,out var found);
                if((found||oldFound) && !Equals(old,current))changes.Add(new(field,old,current));
            }
            return changes;
        }
        catch(JsonException){return [];}
    }
    private static object? ReadField(JsonElement root,string field,out bool found)
    {
        found=false;
        foreach(var property in root.EnumerateObject())
        {
            if(!string.Equals(property.Name,field,StringComparison.OrdinalIgnoreCase))continue;
            var value=property.Value;object? safe=null;
            if(value.ValueKind==JsonValueKind.Null){found=true;return null;}
            if(field is "status" or "priority" or "rollover")
            {
                string[] allowed=field=="priority"?Enum.GetNames<SupportPriority>():field=="rollover"?["None"]:["Built","Published","Revoked","Draft","Issued","Accepted","Expired","Cancelled","Active","Suspended","Open","InProgress","Resolved","Closed","Submitted","Reviewed","Approved","Rejected"];
                if(value.ValueKind==JsonValueKind.String && allowed.Contains(value.GetString(),StringComparer.Ordinal))safe=value.GetString();
            }
            else if(field.EndsWith("Id",StringComparison.Ordinal)||field.EndsWith("By",StringComparison.Ordinal)||field=="assignedTo")
            {if(value.ValueKind==JsonValueKind.String && Guid.TryParse(value.GetString(),out var id))safe=id;}
            else if(field.EndsWith("At",StringComparison.Ordinal))
            {if(value.ValueKind==JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))safe=date.ToUniversalTime();}
            else if(field=="isActive")
            {if(value.ValueKind is JsonValueKind.True or JsonValueKind.False)safe=value.GetBoolean();}
            else if(field=="quotaUnit")
            {if(value.ValueKind==JsonValueKind.String && value.GetString() is "tokens" or "credits" or "requests" or "seconds" or "characters")safe=value.GetString();}
            else if(value.ValueKind==JsonValueKind.Number && value.TryGetDecimal(out var number) && number>=0 && number<=999999999999m)safe=number;
            found=safe is not null;return safe;
        }
        return null;
    }
}
