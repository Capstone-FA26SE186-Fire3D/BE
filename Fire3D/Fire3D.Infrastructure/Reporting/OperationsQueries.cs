using System.Data;
using Fire3D.Application.Authentication;
using Fire3D.Application.Reporting;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.Infrastructure.Reporting;
public sealed class OperationsQueries(Fire3DDbContext db) : IOperationsQueries
{
    private static readonly IReadOnlyDictionary<string,string> Definitions=new Dictionary<string,string>
    {
        ["snapshot"]="Accounts/organizations/buildings: current state at asOf, excludes soft-deleted, includes inactive.",
        ["created"]="Created in UTC [from,to), retains historical soft-deleted records.",
        ["processingJobs"]="Jobs created in [from,to), grouped by kind and current status; not training completion.",
        ["tickets"]="Tickets created in [from,to), grouped by current status; organization response only caller-created tickets.",
        ["consistency"]="Authorization and aggregates share one read-only RepeatableRead PostgreSQL snapshot."
    };
    private async Task<(User? Actor,DateTime AsOf)> BeginSnapshot(Guid actor,Guid family,bool platform,CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY",ct);
        var asOf=await db.Database.SqlQueryRaw<DateTime>("SELECT transaction_timestamp() AS \"Value\"").SingleAsync(ct);
        var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==actor && x.IsActive && x.DeletedAt==null,ct);
        if(user is null || user.Role!=(platform?UserRole.PlatformAdmin:UserRole.OrganizationUser)
            || !await db.Set<RefreshToken>().AnyAsync(x=>x.UserId==actor && x.FamilyId==family && x.ConsumedAt==null && x.RevokedAt==null && x.ExpiresAt>asOf,ct)
            || !platform && !await db.Organizations.AnyAsync(x=>x.Id==user.OrganizationId && x.IsActive && x.DeletedAt==null,ct))return(null,asOf);
        return(user,asOf);
    }
    private sealed record Common(List<LifecycleCount> Buildings,List<ProcessingCount> Jobs,List<TicketCount> Tickets,OrganizationCreated Created);
    private async Task<Common> Aggregate(Guid? organization,Guid? owner,DateTime from,DateTime to,CancellationToken ct)
    {
        var buildings=db.Buildings.AsNoTracking().Where(x=>organization==null || x.OrganizationId==organization);
        var jobs=db.ProcessingJobs.AsNoTracking().Where(x=>x.CreatedAt>=from && x.CreatedAt<to && (organization==null || db.Revisions.Any(r=>r.Id==x.RevisionId && db.Buildings.Any(b=>b.Id==r.BuildingId && b.OrganizationId==organization))));
        var tickets=db.SupportTickets.AsNoTracking().Where(x=>x.CreatedAt>=from && x.CreatedAt<to && (owner==null || x.CreatedBy==owner));
        var counts=await buildings.Where(x=>x.DeletedAt==null).GroupBy(x=>x.IsActive).Select(x=>new LifecycleCount(x.Key,x.Count())).ToListAsync(ct);
        var jobCounts=await jobs.GroupBy(x=>new{x.Kind,x.Status}).Select(x=>new ProcessingCount(x.Key.Kind,x.Key.Status,x.Count())).ToListAsync(ct);
        var ticketCounts=await tickets.GroupBy(x=>x.Status).Select(x=>new TicketCount(x.Key.ToString(),x.Count())).ToListAsync(ct);
        var kinds=new[]{"Geometry","PlaytestPackage","ReleasePackage"}.Concat(jobCounts.Select(x=>x.Kind)).Distinct().Order().ToArray();
        var states=new[]{"Queued","Running","Succeeded","Failed","Cancelled"}.Concat(jobCounts.Select(x=>x.Status)).Distinct().Order().ToArray();
        return new(FillLifecycle(counts),kinds.SelectMany(kind=>states.Select(status=>new ProcessingCount(kind,status,jobCounts.SingleOrDefault(x=>x.Kind==kind && x.Status==status)?.Count??0))).ToList(),
            Enum.GetNames<SupportTicketStatus>().Select(status=>new TicketCount(status,ticketCounts.SingleOrDefault(x=>x.Status==status)?.Count??0)).ToList(),
            new(await buildings.CountAsync(x=>x.CreatedAt>=from && x.CreatedAt<to,ct),jobCounts.Sum(x=>x.Count),ticketCounts.Sum(x=>x.Count)));
    }
    private static List<LifecycleCount> FillLifecycle(List<LifecycleCount> counts)=>new[]{false,true}.Select(active=>new LifecycleCount(active,counts.SingleOrDefault(x=>x.IsActive==active)?.Count??0)).ToList();
    public async Task<AuthResult<PlatformOperations>> Platform(Guid actor,Guid family,string? from,string? to,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var snapshot=await BeginSnapshot(actor,family,true,ct);
        if(snapshot.Actor is null)return AuthResult<PlatformOperations>.Fail("FORBIDDEN","Only an active PlatformAdmin session can read platform operations.",403);
        if(!ReportingRange.TryParse(from,to,snapshot.AsOf,out var range))return AuthResult<PlatformOperations>.Fail("VALIDATION_ERROR","Use timezone-qualified timestamps and a positive range of at most 90 days.",400);
        var users=db.Users.AsNoTracking();var organizations=db.Organizations.AsNoTracking();
        var accountCounts=await users.Where(x=>x.DeletedAt==null).GroupBy(x=>new{x.Role,x.IsActive}).Select(x=>new AccountCount(x.Key.Role.ToString(),x.Key.IsActive,x.Count())).ToListAsync(ct);
        var accounts=Enum.GetNames<UserRole>().SelectMany(role=>new[]{false,true}.Select(active=>new AccountCount(role,active,accountCounts.SingleOrDefault(x=>x.Role==role && x.IsActive==active)?.Count??0))).ToList();
        var orgCounts=await organizations.Where(x=>x.DeletedAt==null).GroupBy(x=>x.IsActive).Select(x=>new LifecycleCount(x.Key,x.Count())).ToListAsync(ct);
        var common=await Aggregate(null,null,range.From,range.To,ct);
        var created=new PlatformCreated(await users.CountAsync(x=>x.CreatedAt>=range.From && x.CreatedAt<range.To,ct),await organizations.CountAsync(x=>x.CreatedAt>=range.From && x.CreatedAt<range.To,ct),common.Created.Buildings,common.Created.ProcessingJobs,common.Created.Tickets);
        await tx.CommitAsync(ct);
        return AuthResult<PlatformOperations>.Ok(new(snapshot.AsOf,range.From,range.To,Definitions,accounts,FillLifecycle(orgCounts),common.Buildings,common.Jobs,common.Tickets,created));
    }
    public async Task<AuthResult<OrganizationOperations>> Organization(Guid actor,Guid family,string? from,string? to,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var snapshot=await BeginSnapshot(actor,family,false,ct);
        if(snapshot.Actor is null)return AuthResult<OrganizationOperations>.Fail("FORBIDDEN","Only an active OrganizationUser session can read tenant operations.",403);
        if(!ReportingRange.TryParse(from,to,snapshot.AsOf,out var range))return AuthResult<OrganizationOperations>.Fail("VALIDATION_ERROR","Use timezone-qualified timestamps and a positive range of at most 90 days.",400);
        var common=await Aggregate(snapshot.Actor.OrganizationId,actor,range.From,range.To,ct);
        await tx.CommitAsync(ct);
        // No account or other-organization aggregate exists in this DTO.
        return AuthResult<OrganizationOperations>.Ok(new(snapshot.AsOf,range.From,range.To,Definitions,common.Buildings,common.Jobs,common.Tickets,common.Created));
    }
}
