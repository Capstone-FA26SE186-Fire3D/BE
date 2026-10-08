using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.Infrastructure.Billing;
public sealed partial class PayosPayments
{
    public async Task<PayosPaymentResponse> Payment(Guid actorId,Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var actor=await ActorFor(actorId,false,ct);var req=await db.PayosPaymentRequests.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();Scope(actor,req.OrganizationId);
        var lines=await db.Set<QuotationBuildingItem>().AsNoTracking().Where(x=>x.QuotationId==req.QuotationId).OrderBy(x=>x.BuildingId).ToListAsync(ct);
        var records=await db.Set<PaymentProvisioningRecord>().AsNoTracking().Where(x=>x.PaymentTransactionId==req.PaidTransactionId).ToListAsync(ct);
        var entitlements=await db.Set<ServiceEntitlement>().AsNoTracking().Where(x=>x.PaymentTransactionId==req.PaidTransactionId&&x.PaymentTransactionId!=null).ToListAsync(ct);
        var items=lines.Select(line=>new ProvisioningLineResponse(line.Id,line.BuildingId,records.SingleOrDefault(x=>x.QuotationItemId==line.Id)?.Status??"NotStarted",
            entitlements.SingleOrDefault(x=>x.QuotationItemId==line.Id)?.Id,records.SingleOrDefault(x=>x.QuotationItemId==line.Id)?.LastError)).ToArray();
        var status=req.Status!=PaymentRequestStatus.Paid?"NotStarted":items.Length==0||items.Any(x=>x.Status is "NeedsReconcile" or "NotStarted")?"NeedsReconcile":items.All(x=>x.Status=="Succeeded")?"Succeeded":"Pending";
        var transaction=req.PaidTransactionId.HasValue?await db.PaymentTransactions.AsNoTracking().SingleAsync(x=>x.Id==req.PaidTransactionId,ct):null;
        return new(req.Id,req.QuotationId,req.OrderCode,req.ExpectedAmount,req.ExpectedCurrency,req.Status.ToString(),req.PaidAt,req.PaidTransactionId,transaction?.Status.ToString(),status,items);
    }
    public async Task<BillingPage<EntitlementResponse>> Entitlements(Guid actorId,Guid? org,Guid? building,int page,int size,CancellationToken ct)
    {
        if(page<1||size is <1 or >100||page>1000000)throw new BillingException(400,"BILLING_PAGINATION_INVALID","page must be positive and pageSize must be 1–100.");
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);var actor=await ActorFor(actorId,false,ct);
        if(actor.Role==UserRole.OrganizationUser){if(org.HasValue&&org!=actor.OrganizationId)throw Missing();org=actor.OrganizationId;}
        var query=from e in db.Set<ServiceEntitlement>().AsNoTracking() join b in db.Buildings on e.BuildingId equals b.Id join o in db.Organizations on e.OrganizationId equals o.Id
            where (!org.HasValue||e.OrganizationId==org)&&(!building.HasValue||e.BuildingId==building)
            select new{e,IsActive=b.IsActive&&b.DeletedAt==null,OrgActive=o.IsActive&&o.DeletedAt==null};
        var total=await query.CountAsync(ct);var now=Now;var list=await query.OrderByDescending(x=>x.e.CreatedAt).ThenBy(x=>x.e.Id).Skip((page-1)*size).Take(size).ToListAsync(ct);
        return new(list.Select(x=>new EntitlementResponse(x.e.Id,x.e.BuildingId,x.e.Status,x.e.Status=="Active"&&x.IsActive&&x.OrgActive&&x.e.StartsAt<=now&&x.e.EndsAt>now,x.e.StartsAt,x.e.EndsAt,x.e.PaymentTransactionId,x.e.CommercialVersion,x.e.LearnerLimit)).ToArray(),total,page,size);
    }
    public async Task Reconcile(Guid actorId,Guid family,Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await Identity(ct);var actor=await ActorFor(actorId,false,ct);await Family(actorId,family,ct);
        if(actor.Role!=UserRole.PlatformAdmin)throw new BillingException(403,"BILLING_ROLE_FORBIDDEN","Only PlatformAdmin can request reconciliation.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM billing_checkout_operations WHERE id={id} FOR UPDATE",ct);
        var op=await db.Set<BillingCheckoutOperation>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
        if(op.Status is "Creating" or "Ready" or "NeedsReconcile")
            await db.Set<BillingCheckoutOperation>().Where(x=>x.Id==id&&(x.LeaseUntil==null||x.LeaseUntil<=Now)).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Attempts,0).SetProperty(x=>x.NextAttemptAt,Now),ct);
        await db.Set<PayosWebhookInboxEntry>().Where(x=>x.OrderCode==op.OrderCode&&(x.Status=="NeedsReconcile"||x.Status=="Pending"&&x.Attempts>=10)&&(x.LeaseUntil==null||x.LeaseUntil<=Now))
            .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Attempts,0).SetProperty(x=>x.NextAttemptAt,Now),ct);
        if(op.PaymentRequestId.HasValue)
        {
            var paid=await db.PayosPaymentRequests.Where(x=>x.Id==op.PaymentRequestId).Select(x=>x.PaidTransactionId).SingleAsync(ct);
            if(paid.HasValue)await db.Set<PaymentProvisioningRecord>().Where(x=>x.PaymentTransactionId==paid&&(x.Status=="NeedsReconcile"||x.Status=="Pending"&&x.Attempts>=10)&&(x.LeaseUntil==null||x.LeaseUntil<=Now))
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Attempts,0).SetProperty(x=>x.NextAttemptAt,Now),ct);
        }
        var organization=await db.Quotations.Where(x=>x.Id==op.QuotationId).Select(x=>x.OrganizationId).SingleAsync(ct);
        Audit(actorId,organization,"ReconcileRequested",id);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
