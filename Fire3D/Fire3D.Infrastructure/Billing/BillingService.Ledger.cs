using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Billing;

public sealed partial class BillingService
{
    private sealed record EntitlementRow(Guid Id,Guid BuildingId,Guid OrganizationId,string Status,DateTime StartsAt,DateTime EndsAt,int CommercialVersion,
        int? LearnerLimit,int CapacityRevision,int? EffectiveLearnerLimit,int SeatsUsed);
    private sealed record UpgradeRow(Guid Id,Guid EntitlementId,int CapacityRevision,int PreviousLearnerLimit,int LearnerLimit,int AdditionalQuotaUnits,DateTime EffectiveFrom,DateTime CreatedAt);
    private sealed record BalanceRow(string QuotaUnit,long Granted,long Reserved,long Consumed,long Expired,long Scheduled);
    private sealed record GrantRow(Guid Id,string SourceKind,string QuotaUnit,int QuotaUnits,DateTime StartsAt,DateTime EndsAt,long Reserved,long Consumed,
        Guid? EntitlementId,Guid? BuildingId,Guid PaymentTransactionId,Guid PolicyVersionId,DateTime CreatedAt);

    /// <summary>Current and upcoming paid service for a Building at one asOf, read in one RepeatableRead snapshot.</summary>
    public async Task<BuildingServiceEntitlementResponse> GetBuildingEntitlement(Guid actor,Guid buildingId,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var identity=await Authorize(actor,false,ct);
        var building=await db.Buildings.AsNoTracking().Where(x=>x.Id==buildingId&&x.DeletedAt==null).Select(x=>new{x.OrganizationId}).SingleOrDefaultAsync(ct)??throw Missing();
        await Scope(identity,building.OrganizationId,ct);
        var asOf=await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var rows=await db.Database.SqlQuery<EntitlementRow>($"""
            SELECT e.id AS "Id",e.building_id AS "BuildingId",e.organization_id AS "OrganizationId",e.status AS "Status",e.starts_at AS "StartsAt",e.ends_at AS "EndsAt",
             e.commercial_version AS "CommercialVersion",e.learner_limit AS "LearnerLimit",billing_capacity_revision(e.id) AS "CapacityRevision",
             billing_effective_learner_limit(e.id,greatest(e.starts_at,{asOf})) AS "EffectiveLearnerLimit",
             (SELECT count(*)::int FROM billing_learner_seats s WHERE s.entitlement_id=e.id) AS "SeatsUsed"
            FROM service_entitlements e
            WHERE e.building_id={buildingId} AND e.payment_transaction_id IS NOT NULL AND e.status IN('Active','Suspended') AND e.ends_at>{asOf}
            ORDER BY e.starts_at,e.id
            """).ToListAsync(ct);
        var ids=rows.Select(x=>x.Id).ToArray();
        var upgrades=ids.Length==0?[]:await db.Database.SqlQuery<UpgradeRow>($"""
            SELECT id AS "Id",entitlement_id AS "EntitlementId",capacity_revision AS "CapacityRevision",previous_learner_limit AS "PreviousLearnerLimit",learner_limit AS "LearnerLimit",
             additional_quota_units AS "AdditionalQuotaUnits",effective_from AS "EffectiveFrom",created_at AS "CreatedAt"
            FROM billing_entitlement_upgrades WHERE entitlement_id = ANY({ids}) ORDER BY entitlement_id,capacity_revision
            """).ToListAsync(ct);
        var organizationActive=await db.Organizations.AnyAsync(x=>x.Id==building.OrganizationId&&x.IsActive&&x.DeletedAt==null,ct);
        var buildingActive=await db.Buildings.AnyAsync(x=>x.Id==buildingId&&x.IsActive&&x.DeletedAt==null,ct);
        ServiceEntitlementView View(EntitlementRow x)=>new(x.Id,x.BuildingId,x.OrganizationId,x.Status,x.StartsAt,x.EndsAt,
            x.Status=="Active"&&organizationActive&&buildingActive&&x.StartsAt<=asOf&&asOf<x.EndsAt,x.CommercialVersion,x.LearnerLimit,x.EffectiveLearnerLimit,
            x.CapacityRevision,x.SeatsUsed,x.EffectiveLearnerLimit is { } limit?Math.Max(0,limit-x.SeatsUsed):null,
            upgrades.Where(u=>u.EntitlementId==x.Id).Select(u=>new EntitlementUpgradeView(u.Id,u.CapacityRevision,u.PreviousLearnerLimit,u.LearnerLimit,u.AdditionalQuotaUnits,u.EffectiveFrom,u.CreatedAt)).ToArray());
        var current=rows.Where(x=>x.StartsAt<=asOf).OrderByDescending(x=>x.StartsAt).FirstOrDefault();
        return new(buildingId,asOf,current is null?null:View(current),rows.Where(x=>x.StartsAt>asOf).Select(View).ToArray());
    }

    private async Task<Guid> QuotaScope(Guid actor,Guid? organizationId,CancellationToken ct)
    {
        var identity=await Authorize(actor,organizationId.HasValue,ct);
        var org=identity.Role==UserRole.PlatformAdmin?organizationId??throw new BillingException(400,"BILLING_VALIDATION_FAILED","Choose an organization.",new(){["organizationId"]=["Required for PlatformAdmin."]})
            :identity.OrganizationId!.Value;
        if(identity.Role==UserRole.PlatformAdmin&&!await db.Organizations.AnyAsync(x=>x.Id==org&&x.DeletedAt==null,ct))throw Missing();
        return org;
    }

    /// <summary>Prepaid balance per unit: active grants minus reserved and settled usage; expired and scheduled reported separately.</summary>
    public async Task<AiQuotaBalanceResponse> GetAiQuota(Guid actor,Guid? organizationId,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var org=await QuotaScope(actor,organizationId,ct);
        var asOf=await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var rows=await db.Database.SqlQuery<BalanceRow>($"""
            SELECT g.quota_unit AS "QuotaUnit",
             COALESCE(sum(g.quota_units) FILTER(WHERE g.starts_at<={asOf} AND g.ends_at>{asOf}),0)::bigint AS "Granted",
             COALESCE(sum(u.reserved) FILTER(WHERE g.starts_at<={asOf} AND g.ends_at>{asOf}),0)::bigint AS "Reserved",
             COALESCE(sum(u.consumed) FILTER(WHERE g.starts_at<={asOf} AND g.ends_at>{asOf}),0)::bigint AS "Consumed",
             COALESCE(sum(greatest(g.quota_units-u.consumed-u.reserved,0)) FILTER(WHERE g.ends_at<={asOf}),0)::bigint AS "Expired",
             COALESCE(sum(g.quota_units) FILTER(WHERE g.starts_at>{asOf}),0)::bigint AS "Scheduled"
            FROM billing_ai_quota_grants g
            CROSS JOIN LATERAL (SELECT COALESCE(sum(a.reserved_units) FILTER(WHERE a.status='Reserved'),0) reserved,
              COALESCE(sum(a.consumed_units) FILTER(WHERE a.status='Settled'),0) consumed FROM billing_ai_quota_allocations a WHERE a.grant_id=g.id) u
            WHERE g.organization_id={org}
            GROUP BY g.quota_unit ORDER BY g.quota_unit
            """).ToListAsync(ct);
        return new(org,asOf,rows.Select(x=>new AiQuotaUnitBalance(x.QuotaUnit,x.Granted,x.Reserved,x.Consumed,x.Expired,x.Granted-x.Reserved-x.Consumed,x.Scheduled)).ToArray());
    }

    public async Task<BillingPage<AiQuotaGrantView>> ListAiQuotaGrants(Guid actor,Guid? organizationId,int page,int pageSize,CancellationToken ct)
    {
        Pagination(page,pageSize);
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var org=await QuotaScope(actor,organizationId,ct);
        var asOf=await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var total=await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM billing_ai_quota_grants WHERE organization_id={org}").SingleAsync(ct);
        var skip=(page-1)*pageSize;
        var rows=await db.Database.SqlQuery<GrantRow>($"""
            SELECT g.id AS "Id",g.source_kind AS "SourceKind",g.quota_unit AS "QuotaUnit",g.quota_units AS "QuotaUnits",g.starts_at AS "StartsAt",g.ends_at AS "EndsAt",
             u.reserved AS "Reserved",u.consumed AS "Consumed",g.entitlement_id AS "EntitlementId",e.building_id AS "BuildingId",
             g.payment_transaction_id AS "PaymentTransactionId",g.policy_version_id AS "PolicyVersionId",g.created_at AS "CreatedAt"
            FROM billing_ai_quota_grants g LEFT JOIN service_entitlements e ON e.id=g.entitlement_id
            CROSS JOIN LATERAL (SELECT COALESCE(sum(a.reserved_units) FILTER(WHERE a.status='Reserved'),0)::bigint reserved,
              COALESCE(sum(a.consumed_units) FILTER(WHERE a.status='Settled'),0)::bigint consumed FROM billing_ai_quota_allocations a WHERE a.grant_id=g.id) u
            WHERE g.organization_id={org} ORDER BY g.starts_at DESC,g.id OFFSET {skip} LIMIT {pageSize}
            """).ToListAsync(ct);
        return new(rows.Select(x=>new AiQuotaGrantView(x.Id,x.SourceKind,x.QuotaUnit,x.QuotaUnits,x.StartsAt,x.EndsAt,
            x.StartsAt>asOf?"Scheduled":x.EndsAt<=asOf?"Expired":"Active",x.Reserved,x.Consumed,
            x.StartsAt<=asOf&&x.EndsAt>asOf?Math.Max(0,x.QuotaUnits-x.Reserved-x.Consumed):0,
            x.EntitlementId,x.BuildingId,x.PaymentTransactionId,x.PolicyVersionId,x.CreatedAt)).ToArray(),total,page,pageSize);
    }

    public async Task<BillingPage<AiUsageView>> ListAiUsage(Guid actor,Guid? organizationId,int page,int pageSize,CancellationToken ct)
    {
        Pagination(page,pageSize);
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var org=await QuotaScope(actor,organizationId,ct);
        var total=await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM billing_ai_quota_allocations WHERE organization_id={org}").SingleAsync(ct);
        var skip=(page-1)*pageSize;
        var rows=await db.Database.SqlQuery<AiUsageView>($"""
            SELECT id AS "AllocationId",request_id AS "RequestId",grant_id AS "GrantId",quota_unit AS "QuotaUnit",reserved_units AS "ReservedUnits",
             consumed_units AS "ConsumedUnits",status AS "Status",created_at AS "CreatedAt",settled_at AS "SettledAt"
            FROM billing_ai_quota_allocations WHERE organization_id={org} ORDER BY created_at DESC,id OFFSET {skip} LIMIT {pageSize}
            """).ToListAsync(ct);
        return new(rows,total,page,pageSize);
    }

    public async Task<EnterpriseQuoteResponse> GetEnterpriseRequest(Guid actor,Guid id,CancellationToken ct)
    {
        var identity=await Authorize(actor,false,ct);
        var item=await db.Set<EnterpriseQuoteRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
        await Scope(identity,item.OrganizationId,ct);return EnterpriseView(item);
    }

    public async Task<EnterpriseQuoteResponse> UpdateEnterpriseStatus(Guid actor,Guid family,Guid id,EnterpriseStatusRequest request,string? ifMatch,CancellationToken ct)
    {
        Field(request.Status is "Contacted" or "Rejected" or "Cancelled","status","Use Contacted, Rejected or Cancelled; Quoted is set by creating a quotation.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,true,ct);
        await Lock("fet3d:billing:enterprise:"+id,false,ct);
        var item=await db.Set<EnterpriseQuoteRequest>().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
        BillingETag.Require(ifMatch,id,item.Revision);
        var allowed=request.Status=="Contacted"?item.Status=="New":item.Status is "New" or "Contacted" or "Quoted";
        if(!allowed)throw Conflict($"An enterprise request in {item.Status} cannot become {request.Status}.");
        var old=new{item.Status,item.Revision};
        item.Status=request.Status;item.Revision++;item.UpdatedAt=DateTime.UtcNow;
        Audit(identity,item.OrganizationId,"enterprise_quote_requests",id,AuditAction.Update,old,new{item.Status,item.Revision});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return EnterpriseView(item);
    }

    /// <summary>Admin drafts a Building quotation for the requesting tenant. No charge or entitlement is created.</summary>
    public async Task<QuotationResponse> CreateEnterpriseQuotation(Guid actor,Guid family,Guid id,EnterpriseQuotationRequest request,string? key,CancellationToken ct)
    {
        var write=ValidateQuotation(new QuotationWriteRequest(request.Items));var hash=Hash(new{id,request=write});var stableKey=Key(key);
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,true,ct);
        var previous=await Receipt(identity,"CreateEnterpriseQuotation",stableKey,hash,ct);
        if(previous is not null)return Replay<QuotationResponse>(previous);
        await Lock("fet3d:billing:enterprise:"+id,false,ct);
        var item=await db.Set<EnterpriseQuoteRequest>().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
        await Scope(identity,item.OrganizationId,ct);
        if(item.Status is not ("New" or "Contacted")||item.QuotationId.HasValue)throw Conflict("This enterprise request was already quoted or closed.");
        if(!await db.Users.AnyAsync(x=>x.Id==item.RequestedBy&&x.Role==UserRole.OrganizationUser&&x.OrganizationId==item.OrganizationId&&x.IsActive&&x.DeletedAt==null,ct))
            throw new BillingException(409,"ENTERPRISE_REQUESTER_UNAVAILABLE","The requesting OrganizationUser is no longer active.");
        var quote=await NewDraft(item.OrganizationId,item.RequestedBy,write,ct);
        var old=new{item.Status,item.Revision};
        item.Status="Quoted";item.QuotationId=quote.Id;item.Revision++;item.UpdatedAt=DateTime.UtcNow;
        var response=await QuoteView(quote,ct);SaveReceipt(identity,"CreateEnterpriseQuotation",stableKey,hash,quote.Id,response);
        Audit(identity,quote.OrganizationId,"quotations",quote.Id,AuditAction.Create,null,new{quote.Status,enterpriseRequestId=id,quote.TotalAmount});
        Audit(identity,item.OrganizationId,"enterprise_quote_requests",id,AuditAction.Update,old,new{item.Status,item.QuotationId,item.Revision});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }
}
