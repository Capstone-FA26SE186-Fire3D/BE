using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.Infrastructure.Billing;

public sealed partial class BillingService
{
    private static QuotaPolicyResponse PolicyView(BillingQuotaPolicy p)=>new(p.Id,p.Audience,p.PolicyKind,p.QuotaUnit,p.EffectiveFrom,p.EffectiveUntil,p.Rollover);
    public async Task<QuotaPolicyResponse> CreateQuotaPolicy(Guid actor,Guid family,QuotaPolicyWriteRequest request,CancellationToken ct)
    {
        Field(request.QuotaUnit is not null && System.Text.RegularExpressions.Regex.IsMatch(request.QuotaUnit,"^[a-z][a-z0-9_-]{0,49}$"),"quotaUnit","Use a lowercase quota unit of 1-50 letters/digits/underscores/hyphens.");
        Field(request.EffectiveFrom!=default && (request.EffectiveUntil is null || request.EffectiveUntil>request.EffectiveFrom),"effectiveUntil","Policy interval must be valid.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,true,ct);
        var p=new BillingQuotaPolicy {Id=Guid.NewGuid(),QuotaUnit=request.QuotaUnit!,EffectiveFrom=request.EffectiveFrom.UtcDateTime,EffectiveUntil=request.EffectiveUntil?.UtcDateTime,CreatedBy=actor,CreatedAt=DateTime.UtcNow};
        db.BillingQuotaPolicies.Add(p);var response=PolicyView(p);Audit(identity,null,"billing_quota_policy_versions",p.Id,AuditAction.Create,null,response);
        await RequireLiveFamily(actor,family,ct);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }
    public async Task<BillingPage<QuotaPolicyResponse>> ListQuotaPolicies(Guid actor,int page,int pageSize,CancellationToken ct)
    {
        await Authorize(actor,true,ct);Pagination(page,pageSize);var query=db.BillingQuotaPolicies.AsNoTracking();var total=await query.CountAsync(ct);
        var items=await query.OrderByDescending(x=>x.CreatedAt).ThenBy(x=>x.Id).Skip((page-1)*pageSize).Take(pageSize).ToListAsync(ct);
        return new(items.Select(PolicyView).ToArray(),total,page,pageSize);
    }
    public async Task<QuotaPolicyResponse> GetQuotaPolicy(Guid actor,Guid id,CancellationToken ct)
    {await Authorize(actor,true,ct);return PolicyView(await db.BillingQuotaPolicies.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing());}
}
