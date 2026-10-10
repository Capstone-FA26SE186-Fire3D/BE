using Fire3D.Application.Authentication;
using Fire3D.Application.Reporting;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.Infrastructure.Reporting;
public sealed class AuditQueries(Fire3DDbContext db) : IAuditQueries
{
    private async Task<bool> Authorized(Guid actor,Guid family,CancellationToken ct)=>
        await db.Users.AnyAsync(x=>x.Id==actor && x.Role==UserRole.PlatformAdmin && x.IsActive && x.DeletedAt==null,ct)
        && await db.Set<RefreshToken>().AnyAsync(x=>x.UserId==actor && x.FamilyId==family && x.ConsumedAt==null && x.RevokedAt==null && x.ExpiresAt>DateTime.UtcNow,ct);
    private static AuditMetadata Metadata(AuditLog x)=>new(x.Id,x.UserId,x.OrganizationId,x.Action.ToString(),x.TargetEntity,x.TargetId,x.CorrelationId,x.CreatedAt);
    public async Task<AuthResult<AuditPage>> List(Guid actor,Guid family,AuditFilters f,CancellationToken ct)
    {
        if(!await Authorized(actor,family,ct))return AuthResult<AuditPage>.Fail("FORBIDDEN","Only an active PlatformAdmin session can read audit.",403);
        var action=default(AuditAction);
        if(f.Page is <1 or >100000 || f.PageSize is <1 or >100 || !ReportingRange.TryParse(f.From,f.To,DateTime.UtcNow,out var range)
            || f.Action is not null && (!Enum.TryParse(f.Action,true,out action)||!Enum.IsDefined(action)||!Enum.GetNames<AuditAction>().Contains(f.Action,StringComparer.OrdinalIgnoreCase))
            || f.TargetEntity is not null && !System.Text.RegularExpressions.Regex.IsMatch(f.TargetEntity,@"^[A-Za-z][A-Za-z0-9_]{0,99}$"))
            return AuthResult<AuditPage>.Fail("VALIDATION_ERROR","Use named filters, timezone-qualified timestamps and a positive range of at most 90 days.",400);
        var query=db.AuditLogs.AsNoTracking().Where(x=>x.CreatedAt>=range.From && x.CreatedAt<range.To);
        if(f.ActorId.HasValue)query=query.Where(x=>x.UserId==f.ActorId);
        if(f.OrganizationId.HasValue)query=query.Where(x=>x.OrganizationId==f.OrganizationId);
        if(f.Action is not null)query=query.Where(x=>x.Action==action);
        if(f.TargetEntity is not null)query=query.Where(x=>x.TargetEntity==f.TargetEntity);
        if(f.TargetId.HasValue)query=query.Where(x=>x.TargetId==f.TargetId);
        if(f.CorrelationId.HasValue)query=query.Where(x=>x.CorrelationId==f.CorrelationId);
        var total=await query.CountAsync(ct);
        var rows=await query.OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).Skip((f.Page-1)*f.PageSize).Take(f.PageSize)
            .Select(x=>new AuditMetadata(x.Id,x.UserId,x.OrganizationId,x.Action.ToString(),x.TargetEntity,x.TargetId,x.CorrelationId,x.CreatedAt)).ToListAsync(ct);
        return AuthResult<AuditPage>.Ok(new(rows,total,f.Page,f.PageSize,range.From,range.To));
    }
    public async Task<AuthResult<AuditDetail>> Get(Guid actor,Guid family,Guid id,CancellationToken ct)
    {
        if(!await Authorized(actor,family,ct))return AuthResult<AuditDetail>.Fail("FORBIDDEN","Only an active PlatformAdmin session can read audit.",403);
        var item=await db.AuditLogs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);
        return item is null?AuthResult<AuditDetail>.Fail("NOT_FOUND","The audit record was not found.",404):AuthResult<AuditDetail>.Ok(new(Metadata(item),SafeAuditChanges.Read(item)));
    }
}
