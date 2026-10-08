using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Billing;

public sealed partial class BillingService
{
    private async Task PinServicePeriods(Quotation quote,IssueQuotationRequest request,CancellationToken ct)
    {
        var lines=db.ChangeTracker.Entries<QuotationBuildingItem>().Where(x=>x.State!=EntityState.Deleted&&x.Entity.QuotationId==quote.Id).Select(x=>x.Entity).OrderBy(x=>x.BuildingId).ToArray();
        Field(request.Items is not null&&request.Items.Count==lines.Length&&request.Items.Select(x=>x.QuotationItemId).Distinct().Count()==lines.Length
            &&request.Items.All(x=>lines.Any(l=>l.Id==x.QuotationItemId)),"items","Supply one service-period selection per quotation item.");
        var now=DateTime.UtcNow;
        foreach(var line in lines)
        {
            var selected=request.Items!.Single(x=>x.QuotationItemId==line.Id);
            var last=line.PurchaseAction=="Renewal"
                ? await db.Set<ServiceEntitlement>().Where(x=>x.BuildingId==line.BuildingId&&x.PaymentTransactionId!=null&&x.Status!="Revoked"&&x.Status!="Trial").MaxAsync(x=>(DateTime?)x.EndsAt,ct)
                : null;
            DateTime start;
            if(last>now)
            {
                start=last.Value;
                Field(selected.StartsAt is null||selected.StartsAt.Value.UtcDateTime==start,"items","An ongoing Renewal starts at the end of its last committed period.");
            }
            else
            {
                Field(selected.StartsAt.HasValue&&selected.StartsAt.Value.UtcDateTime>now,"items","New or expired Renewal requires an explicit future startsAt with timezone.");
                start=selected.StartsAt!.Value.UtcDateTime;
            }
            DateTime end;
            try{end=start.AddMonths(line.ServiceDurationMonths);}
            catch(ArgumentOutOfRangeException){throw new BillingException(400,"BILLING_VALIDATION_FAILED","Service interval is outside the supported range.",new(){["items"]=["Choose a supported service start."]});}
            if(line.AiQuotaUnits>0)
            {
                var policy=await db.BillingQuotaPolicies.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==line.AiPolicyVersionId,ct);
                if(policy is null||policy.Audience!="organization"||policy.PolicyKind!="quota"||policy.QuotaUnit!=line.AiQuotaUnit||policy.EffectiveFrom>start||policy.EffectiveUntil<end)
                    throw new BillingException(409,"BILLING_POLICY_INTERVAL_INVALID","Quota policy must cover the complete service interval.");
            }
            line.StartsAt=start;line.EndsAt=end;
            line.TermsSnapshot=System.Text.Json.JsonSerializer.Serialize(new{text=request.Terms!.Trim(),startsAt=start,endsAt=end,line.LearnerLimit,line.AiQuotaUnits,line.AiPolicyVersionId,line.AiQuotaUnit,rollover="None"});
        }
        Field(quote.ValidUntil<=lines.Min(x=>x.StartsAt!.Value),"validUntil","Payment deadline cannot exceed the earliest service start.");
    }
}
