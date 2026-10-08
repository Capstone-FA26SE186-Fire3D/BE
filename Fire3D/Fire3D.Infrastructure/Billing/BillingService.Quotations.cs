using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Billing;

public sealed partial class BillingService
{
    private async Task<Actor> QuotationActor(Guid actor,Guid family,bool admin,CancellationToken ct)
    {
        await Lock("fire3d:auth:"+actor,false,ct);
        var identity=await Authorize(actor,admin,ct);
        if(family==Guid.Empty || !await db.Set<RefreshToken>().AnyAsync(x=>x.UserId==actor&&x.FamilyId==family&&x.RevokedAt==null&&x.ConsumedAt==null&&x.ExpiresAt>DateTime.UtcNow,ct))
            throw new BillingException(401,"SESSION_REVOKED","The session is no longer active.");
        return identity;
    }
    private sealed record PriceSnapshot(string BuildingName,string BuildingAddress,string PackageName,decimal MonthlyUnitPrice,int DurationMonths,long PackageRevision);
    private static QuotationWriteRequest ValidateQuotation(QuotationWriteRequest request)
    {
        Field(request.Items is {Count:>=1 and <=100},"items","Choose between 1 and 100 Building lines; larger requests use enterprise quotation.");
        Field(request.Items.All(x=>x is not null&&x.BuildingId!=Guid.Empty&&x.ServicePackageId!=Guid.Empty&&x.PurchaseAction is "New" or "Renewal"),"items","Each line requires Building/package IDs and New or Renewal.");
        Field(request.Items.Select(x=>x.BuildingId).Distinct().Count()==request.Items.Count,"items","A Building may appear only once.");
        return request with {Items=request.Items.OrderBy(x=>x.BuildingId).ToArray()};
    }
    private async Task<Quotation> Quote(Actor actor,Guid id,bool locked,CancellationToken ct)
    {
        if(locked)
        {
            await Lock("fet3d:billing:quotation:"+id,false,ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM quotations WHERE id={id} FOR UPDATE",ct);
        }
        var item=await db.Quotations.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
        await Scope(actor,item.OrganizationId,ct);return item;
    }
    private async Task<QuotationResponse> QuoteView(Quotation quote,CancellationToken ct)
    {
        var items=await db.Set<QuotationBuildingItem>().AsNoTracking().Where(x=>x.QuotationId==quote.Id).OrderBy(x=>x.BuildingId).ToListAsync(ct);
        var lines=items.Select(x=>
        {
            var snapshot=JsonSerializer.Deserialize<PriceSnapshot>(x.PriceSnapshot);
            return new QuotationLineResponse(x.Id,x.BuildingId,x.ServicePackageId,x.PurchaseAction,x.ServiceDurationMonths,
                snapshot?.BuildingName??"",snapshot?.BuildingAddress??"",snapshot?.PackageName??"",x.UnitPrice,x.SubtotalAmount,x.DiscountAmount,x.TotalAmount,
                x.CommercialVersion,x.PackageRevision,x.LearnerLimit,x.AiQuotaUnits,x.AiPolicyVersionId,x.AiQuotaUnit,x.StartsAt,x.EndsAt);
        }).ToArray();
        var terms=JsonDocument.Parse(quote.TermsSnapshot).RootElement;
        return new(quote.Id,quote.OrganizationId,quote.QuotationNumber,quote.Status.ToString(),quote.Revision,quote.Currency,
            quote.SubtotalAmount,quote.DiscountAmount,quote.TaxAmount,quote.TotalAmount,quote.DiscountRuleId,
            terms.TryGetProperty("text",out var text)?text.GetString():null,quote.ValidUntil,quote.AcceptedAt,lines,quote.CommercialVersion);
    }
    private async Task PriceLines(Quotation quote,IReadOnlyList<QuotationItemRequest> requested,CancellationToken ct)
    {
        await Lock("fet3d:billing:catalog",true,ct);
        var existing=await db.Set<QuotationBuildingItem>().Where(x=>x.QuotationId==quote.Id).ToListAsync(ct);
        var now=DateTime.UtcNow;var lines=new List<QuotationBuildingItem>();
        foreach(var input in requested.OrderBy(x=>x.BuildingId))
        {
            await Lock("fire3d:building:"+input.BuildingId,true,ct);
            var building=await db.Buildings.AsNoTracking().Where(x=>x.Id==input.BuildingId&&x.OrganizationId==quote.OrganizationId&&x.IsActive&&x.DeletedAt==null)
                .Select(x=>new {x.Name,Address=x.BuildingLocation==null?null:x.BuildingLocation.Address}).SingleOrDefaultAsync(ct);
            if(building is null||!Text(building.Name,255)||!Text(building.Address,10000))throw Missing();
            var package=await db.ServicePackages.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==input.ServicePackageId&&x.IsActive,ct)??throw Missing();
            if(!PackageView(package).IsPurchasable)throw new BillingException(409,"PACKAGE_NOT_PURCHASABLE","Choose a fully configured v7 package.");
            BillingQuotaPolicy? policy=null;
            if(package.AiPolicyVersionId.HasValue)
            {
                policy=await db.BillingQuotaPolicies.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==package.AiPolicyVersionId&&x.EffectiveFrom<=now&&(x.EffectiveUntil==null||x.EffectiveUntil>now),ct);
                if(policy is null)throw new BillingException(409,"BILLING_POLICY_INVALID","Package policy is no longer effective.");
            }
            if(package.Currency!="VND"||package.DurationMonths is null or <=0||!Money(package.UnitPrice))throw Conflict("The package is not a valid VND Building subscription.");
            var hasPaid=await db.Set<ServiceEntitlement>().AnyAsync(x=>x.BuildingId==input.BuildingId&&x.OrganizationId==quote.OrganizationId&&x.PaymentTransactionId!=null&&x.Status!="Trial",ct);
            if(hasPaid!=(input.PurchaseAction=="Renewal"))throw Conflict(hasPaid?"Choose Renewal for a previously paid Building.":"Renewal requires previous paid Building service.");
            var subtotal=package.UnitPrice*package.DurationMonths.Value;Field(Money(subtotal),"items","The line amount exceeds the supported VND range.");
            var line=existing.SingleOrDefault(x=>x.BuildingId==input.BuildingId)??new QuotationBuildingItem{Id=Guid.NewGuid(),QuotationId=quote.Id,BuildingId=input.BuildingId,CreatedAt=now};
            line.ServicePackageId=package.Id;line.PurchaseAction=input.PurchaseAction;line.ServiceDurationMonths=package.DurationMonths.Value;
            line.CommercialVersion=7;line.PackageRevision=package.Revision;line.LearnerLimit=package.LearnerLimit;
            line.AiQuotaUnits=package.AiQuotaUnits;line.AiPolicyVersionId=package.AiPolicyVersionId;line.AiQuotaUnit=policy?.QuotaUnit;
            line.StartsAt=null;line.EndsAt=null;
            line.UnitPrice=package.UnitPrice;line.SubtotalAmount=subtotal;line.DiscountAmount=0;line.TotalAmount=subtotal;line.Currency="VND";
            line.PriceSnapshot=JsonSerializer.Serialize(new PriceSnapshot(building.Name,building.Address!,package.Name,package.UnitPrice,package.DurationMonths.Value,package.Revision));
            line.TermsSnapshot=quote.TermsSnapshot;line.DiscountSnapshot="{}";line.LineProvisioningKey="line:"+line.Id;line.UpdatedAt=now;
            if(!existing.Contains(line))db.Add(line);lines.Add(line);
        }
        db.RemoveRange(existing.Where(x=>!lines.Contains(x)));
        Field(Money(lines.Sum(x=>x.SubtotalAmount)),"items","Quotation subtotal exceeds the supported VND range.");
        var rules=await db.Set<ServicePackageDiscountRule>().AsNoTracking().Where(x=>x.IsActive&&x.ValidFrom<=now&&(x.ValidUntil==null||x.ValidUntil>now)).ToListAsync(ct);
        var candidates=rules.Select(rule=>
        {
            var eligible=lines.Where(x=>(rule.ServicePackageId==null||rule.ServicePackageId==x.ServicePackageId)
                &&(rule.MinimumDurationMonths==null||x.ServiceDurationMonths>=rule.MinimumDurationMonths)).ToArray();
            var basis=eligible.Sum(x=>x.SubtotalAmount);
            var discount=eligible.Length<rule.MinimumBuildings?0:Math.Min(basis,rule.DiscountKind=="Percent"?
                decimal.Round(basis*rule.DiscountValue/100m,0,MidpointRounding.AwayFromZero):rule.DiscountCurrency=="VND"?rule.DiscountValue:0);
            return new {Rule=rule,Eligible=eligible,Basis=basis,Discount=discount};
        }).Where(x=>x.Discount>0).OrderByDescending(x=>x.Discount).ThenBy(x=>x.Rule.Id).FirstOrDefault();
        quote.DiscountRuleId=candidates?.Rule.Id;quote.DiscountSnapshot="{}";
        if(candidates is not null)
        {
            var snapshot=JsonSerializer.Serialize(new {ruleId=candidates.Rule.Id,candidates.Rule.DiscountKind,candidates.Rule.DiscountValue,eligibleLineIds=candidates.Eligible.Select(x=>x.Id).OrderBy(x=>x).ToArray(),amount=candidates.Discount,rounding="VND-away-from-zero;proportional-floor;remainder-by-line-id"});
            quote.DiscountSnapshot=snapshot;
            foreach(var line in candidates.Eligible)line.DiscountAmount=decimal.Floor(candidates.Discount*line.SubtotalAmount/candidates.Basis);
            var remainder=candidates.Discount-candidates.Eligible.Sum(x=>x.DiscountAmount);
            foreach(var line in candidates.Eligible.OrderBy(x=>x.Id))
                if(remainder>0&&line.DiscountAmount<line.SubtotalAmount){line.DiscountAmount++;remainder--;}
            foreach(var line in candidates.Eligible)line.DiscountSnapshot=snapshot;
        }
        foreach(var line in lines)line.TotalAmount=line.SubtotalAmount-line.DiscountAmount;
        quote.CommercialVersion=7;quote.Quantity=lines.Count;quote.UnitPrice=lines.Count==1?lines[0].UnitPrice:0;quote.Currency="VND";
        quote.SubtotalAmount=lines.Sum(x=>x.SubtotalAmount);quote.DiscountAmount=lines.Sum(x=>x.DiscountAmount);
        quote.TotalAmount=quote.SubtotalAmount-quote.DiscountAmount+quote.TaxAmount;
        Field(Money(quote.TotalAmount),"taxAmount","Quotation total exceeds the supported VND range.");
        quote.PriceSnapshot=JsonSerializer.Serialize(new {currency="VND",lineIds=lines.Select(x=>x.Id).OrderBy(x=>x).ToArray(),quote.SubtotalAmount,quote.DiscountAmount,quote.TaxAmount,quote.TotalAmount});
        quote.UpdatedAt=now;
    }
    public async Task<QuotationResponse> CreateQuotation(Guid actor,Guid family,QuotationWriteRequest request,string? key,CancellationToken ct)
    {
        request=ValidateQuotation(request);var inputHash=Hash(request);var stableKey=Key(key);
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,false,ct);
        if(identity.Role!=UserRole.OrganizationUser)throw new BillingException(403,"ORGANIZATION_USER_REQUIRED","An OrganizationUser creates their own Building quotation.");
        var previous=await Receipt(identity,"CreateQuotation",stableKey,inputHash,ct);
        if(previous is not null){var replay=Replay<QuotationResponse>(previous);await Scope(identity,replay.OrganizationId,ct);return replay;}
        var now=DateTime.UtcNow;var quote=new Quotation {Id=Guid.NewGuid(),OrganizationId=identity.OrganizationId!.Value,RequestedBy=actor,
            QuotationNumber="FET-"+Guid.NewGuid().ToString("N"),Status=QuotationStatus.Draft,CreatedAt=now,UpdatedAt=now,ValidUntil=now.AddDays(7),Currency="VND"};
        db.Quotations.Add(quote);await PriceLines(quote,request.Items,ct);await db.SaveChangesAsync(ct);
        var response=await QuoteView(quote,ct);SaveReceipt(identity,"CreateQuotation",stableKey,inputHash,quote.Id,response);
        Audit(identity,quote.OrganizationId,"quotations",quote.Id,AuditAction.Create,null,new {quote.Status,quote.Quantity,quote.TotalAmount,quote.Revision});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }
    public async Task<QuotationResponse> UpdateDraft(Guid actor,Guid family,Guid id,QuotationWriteRequest request,string? ifMatch,CancellationToken ct)
    {
        request=ValidateQuotation(request);await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);
        var identity=await QuotationActor(actor,family,false,ct);var quote=await Quote(identity,id,true,ct);BillingETag.Require(ifMatch,id,quote.Revision);
        if(quote.Status!=QuotationStatus.Draft)throw Conflict("Only Draft quotation lines can change.");
        var old=new {quote.Quantity,quote.TotalAmount,quote.Revision};await PriceLines(quote,request.Items,ct);quote.Revision++;
        Audit(identity,quote.OrganizationId,"quotations",id,AuditAction.Update,old,new {quote.Quantity,quote.TotalAmount,quote.Revision});
        await db.SaveChangesAsync(ct);var response=await QuoteView(quote,ct);await tx.CommitAsync(ct);return response;
    }
    public async Task<QuotationResponse> GetQuotation(Guid actor,Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var identity=await Authorize(actor,false,ct);var quote=await Quote(identity,id,false,ct);return await QuoteView(quote,ct);
    }
    public async Task<BillingPage<QuotationResponse>> ListQuotations(Guid actor,int page,int pageSize,CancellationToken ct)
    {
        Pagination(page,pageSize);await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);var identity=await Authorize(actor,false,ct);
        var query=db.Quotations.AsNoTracking().Where(x=>identity.Role==UserRole.PlatformAdmin||x.OrganizationId==identity.OrganizationId);
        var total=await query.CountAsync(ct);var items=await query.OrderByDescending(x=>x.CreatedAt).ThenBy(x=>x.Id).Skip((page-1)*pageSize).Take(pageSize).ToListAsync(ct);
        var result=new List<QuotationResponse>();foreach(var item in items)result.Add(await QuoteView(item,ct));return new(result,total,page,pageSize);
    }
    public async Task<QuotationResponse> IssueQuotation(Guid actor,Guid family,Guid id,IssueQuotationRequest request,string? ifMatch,CancellationToken ct)
    {
        Field(request.TaxAmount.HasValue&&Money(request.TaxAmount.Value),"taxAmount","Supply an explicit non-negative whole VND tax amount.");
        Field(Text(request.Terms,10000),"terms","Terms are required, maximum 10,000 characters.");
        Field(request.ValidUntil.HasValue&&request.ValidUntil>DateTimeOffset.UtcNow,"validUntil","Supply a future quotation expiry with timezone.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,true,ct);
        var quote=await Quote(identity,id,true,ct);BillingETag.Require(ifMatch,id,quote.Revision);if(quote.Status!=QuotationStatus.Draft)throw Conflict("Only Draft quotations can be issued.");
        var requested=await db.Set<QuotationBuildingItem>().AsNoTracking().Where(x=>x.QuotationId==id).OrderBy(x=>x.BuildingId).Select(x=>new QuotationItemRequest(x.BuildingId,x.ServicePackageId,x.PurchaseAction)).ToListAsync(ct);
        if(requested.Count==0)throw Conflict("Legacy quotations without Building lines cannot be issued. Create a new quotation.");
        quote.TaxAmount=request.TaxAmount!.Value;quote.TermsSnapshot=JsonSerializer.Serialize(new {text=request.Terms!.Trim()});quote.ValidUntil=request.ValidUntil!.Value.UtcDateTime;
        await PriceLines(quote,requested,ct);if(quote.TotalAmount==0)throw new BillingException(409,"ZERO_AMOUNT_NOT_SUPPORTED","Zero-amount quotations do not have a supported payment/provisioning flow.");
        await PinServicePeriods(quote,request,ct);
        // Persist line snapshots while still Draft, then transition under the same transaction/row lock.
        await db.SaveChangesAsync(ct);quote.Status=QuotationStatus.Issued;quote.IssuedBy=actor;quote.IssuedAt=DateTime.UtcNow;quote.Revision++;
        Audit(identity,quote.OrganizationId,"quotations",id,AuditAction.Update,new {status="Draft"},new {status="Issued",quote.TotalAmount,quote.Revision});
        await db.SaveChangesAsync(ct);var response=await QuoteView(quote,ct);await tx.CommitAsync(ct);return response;
    }
    public async Task<QuotationResponse> AcceptQuotation(Guid actor,Guid family,Guid id,string? ifMatch,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,false,ct);
        if(identity.Role!=UserRole.OrganizationUser)throw new BillingException(403,"ORGANIZATION_USER_REQUIRED","Only the purchasing OrganizationUser can accept terms.");
        var quote=await Quote(identity,id,true,ct);BillingETag.Require(ifMatch,id,quote.Revision);
        if(quote.Status!=QuotationStatus.Issued||quote.ValidUntil<=DateTime.UtcNow)throw Conflict("Only an unexpired Issued quotation can be accepted.");
        quote.Status=QuotationStatus.Accepted;quote.AcceptedAt=DateTime.UtcNow;quote.UpdatedAt=DateTime.UtcNow;quote.Revision++;
        Audit(identity,quote.OrganizationId,"quotations",id,AuditAction.Update,new {status="Issued"},new {status="Accepted",quote.AcceptedAt,quote.Revision});
        await db.SaveChangesAsync(ct);var response=await QuoteView(quote,ct);await tx.CommitAsync(ct);return response;
    }
}
