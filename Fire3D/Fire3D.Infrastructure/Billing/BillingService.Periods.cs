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
            if(line.PurchaseAction=="Upgrade"){await PinUpgrade(quote,line,selected,request.Terms!,now,ct);continue;}
            Field(selected.LearnerLimit is null&&selected.AdditionalQuotaUnits is null&&selected.AiPolicyVersionId is null&&selected.OneTimePrice is null,
                "items","Learner limit, additional quota and one-time price apply only to Upgrade lines.");
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

    // Upgrade keeps the entitlement period: effective time inside it, higher limit, Admin one-time fee, optional bundled quota.
    private async Task PinUpgrade(Quotation quote,QuotationBuildingItem line,QuotationPeriodRequest selected,string terms,DateTime now,CancellationToken ct)
    {
        var entitlement=await db.Set<ServiceEntitlement>().AsNoTracking().SingleAsync(x=>x.Id==line.UpgradeEntitlementId,ct);
        Field(selected.StartsAt is {} at&&at.UtcDateTime>now&&at.UtcDateTime>=quote.ValidUntil&&at.UtcDateTime<entitlement.EndsAt,
            "items","Upgrade startsAt (effective time) must be future, not before validUntil and before the entitlement end.");
        var start=selected.StartsAt!.Value.UtcDateTime;
        var limit=selected.LearnerLimit??line.LearnerLimit;
        Field(limit>line.UpgradePreviousLearnerLimit,"items","Upgrade learner limit must exceed the current learner limit.");
        Field(selected.OneTimePrice is {} price&&Money(price)&&price>0,"items","Upgrade requires a positive whole-VND oneTimePrice.");
        var extra=selected.AdditionalQuotaUnits??0;
        Field(extra>=0,"items","Additional quota units cannot be negative; purchase standalone quota with an AI top-up.");
        BillingQuotaPolicy? policy=null;
        if(extra>0)
        {
            policy=await db.BillingQuotaPolicies.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==selected.AiPolicyVersionId,ct);
            if(policy is null||policy.Audience!="organization"||policy.PolicyKind!="quota"||policy.EffectiveFrom>start||policy.EffectiveUntil<entitlement.EndsAt)
                throw new BillingException(409,"BILLING_POLICY_INTERVAL_INVALID","Quota policy must cover the upgrade effective time through the entitlement end.");
        }
        else Field(selected.AiPolicyVersionId is null,"items","A quota policy applies only when additionalQuotaUnits is positive.");
        line.StartsAt=start;line.EndsAt=entitlement.EndsAt;line.LearnerLimit=limit;
        line.AiQuotaUnits=extra;line.AiPolicyVersionId=policy?.Id;line.AiQuotaUnit=policy?.QuotaUnit;
        line.UnitPrice=selected.OneTimePrice!.Value;line.SubtotalAmount=line.UnitPrice;line.DiscountAmount=0;line.TotalAmount=line.UnitPrice;line.DiscountSnapshot="{}";
        line.TermsSnapshot=System.Text.Json.JsonSerializer.Serialize(new{text=terms.Trim(),pricingBasis="OneTime",entitlementId=entitlement.Id,capacityRevision=line.UpgradeBaseCapacityRevision,
            previousLearnerLimit=line.UpgradePreviousLearnerLimit,learnerLimit=limit,effectiveAt=start,endsAt=entitlement.EndsAt,additionalQuotaUnits=extra,aiPolicyVersionId=policy?.Id,aiQuotaUnit=policy?.QuotaUnit,rollover="None"});
    }

    // Top-up pins policy, unit, units, amount and an Admin-chosen interval inside the policy validity.
    private async Task PinTopUp(Quotation quote,IssueQuotationRequest request,CancellationToken ct)
    {
        Field(request.Items is null or {Count:0},"items","An AI quota top-up has no Building service periods.");
        var topUp=request.TopUp;
        Field(topUp is not null,"topUp","Supply policyVersionId, quotaUnits, amount, startsAt and endsAt.");
        Field(topUp!.QuotaUnits>0,"topUp.quotaUnits","Quota units must be positive.");
        Field(Money(topUp.Amount)&&topUp.Amount>0,"topUp.amount","Amount must be a positive whole VND value.");
        Field(topUp.EndsAt>topUp.StartsAt,"topUp.endsAt","End must be after start.");
        Field(topUp.StartsAt.UtcDateTime>=quote.ValidUntil,"topUp.startsAt","The top-up starts no earlier than the payment deadline.");
        var policy=await db.BillingQuotaPolicies.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==topUp.PolicyVersionId,ct)
            ??throw new BillingException(400,"BILLING_POLICY_INVALID","Choose an existing quota policy.",new(){["topUp.policyVersionId"]=["Policy is absent."]});
        var start=topUp.StartsAt.UtcDateTime;var end=topUp.EndsAt.UtcDateTime;
        if(policy.Audience!="organization"||policy.PolicyKind!="quota"||policy.EffectiveFrom>start||policy.EffectiveUntil<end)
            throw new BillingException(409,"BILLING_POLICY_INTERVAL_INVALID","Quota policy must cover the complete top-up interval.");
        var item=await db.Set<QuotationTopUpItem>().SingleAsync(x=>x.QuotationId==quote.Id,ct);
        item.PolicyVersionId=policy.Id;item.QuotaUnit=policy.QuotaUnit;item.QuotaUnits=topUp.QuotaUnits;item.Amount=topUp.Amount;item.StartsAt=start;item.EndsAt=end;item.UpdatedAt=DateTime.UtcNow;
        quote.CommercialVersion=7;quote.Quantity=1;quote.UnitPrice=topUp.Amount;quote.SubtotalAmount=topUp.Amount;quote.DiscountAmount=0;quote.DiscountRuleId=null;quote.DiscountSnapshot="{}";
        quote.TotalAmount=topUp.Amount+quote.TaxAmount;
        Field(Money(quote.TotalAmount),"taxAmount","Quotation total exceeds the supported VND range.");
        quote.PriceSnapshot=System.Text.Json.JsonSerializer.Serialize(new{currency="VND",purpose=TopUpPurpose,lineIds=new[]{item.Id},quote.SubtotalAmount,quote.DiscountAmount,quote.TaxAmount,quote.TotalAmount});
        quote.TermsSnapshot=System.Text.Json.JsonSerializer.Serialize(new{text=request.Terms!.Trim(),policyVersionId=policy.Id,quotaUnit=policy.QuotaUnit,quotaUnits=topUp.QuotaUnits,startsAt=start,endsAt=end,rollover="None"});
        quote.UpdatedAt=DateTime.UtcNow;
    }
}
