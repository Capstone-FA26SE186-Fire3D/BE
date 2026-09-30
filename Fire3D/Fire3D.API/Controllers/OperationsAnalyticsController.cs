using Fire3D.API.Authorization;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.API.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OperationsAnalyticsController(Fire3DDbContext db) : ControllerBase
{
    [HttpGet("api/admin/analytics/operations")]
    public async Task<IActionResult> Platform([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        if (!TryRange(from, to, out var range, out var error)) return error!;
        return Ok(await Snapshot(null, null, range.From, range.To, ct));
    }

    [HttpGet("api/organizations/me/analytics/operations")]
    public async Task<IActionResult> Organization([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == User.GetActorId() && x.IsActive && x.DeletedAt == null, ct);
        if (actor?.Role != UserRole.OrganizationUser || actor.OrganizationId is not Guid org) return Forbid();
        if (!TryRange(from, to, out var range, out var error)) return error!;
        return Ok(await Snapshot(org, actor.Id, range.From, range.To, ct));
    }

    private async Task<object> Snapshot(Guid? organizationId, Guid? ticketOwnerId, DateTime from, DateTime to, CancellationToken ct)
    {
        var users = db.Users.AsNoTracking().Where(x => x.DeletedAt == null && (organizationId == null || x.OrganizationId == organizationId));
        var buildings = db.Buildings.AsNoTracking().Where(x => x.DeletedAt == null && (organizationId == null || x.OrganizationId == organizationId));
        var jobs = db.ProcessingJobs.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to && (organizationId == null || db.Revisions.Any(r => r.Id == x.RevisionId && db.Buildings.Any(b => b.Id == r.BuildingId && b.OrganizationId == organizationId))));
        var tickets = db.SupportTickets.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to && (ticketOwnerId == null || x.CreatedBy == ticketOwnerId));
        return new { asOf = DateTime.UtcNow, from, to, definitions = new { accounts = "current snapshot", buildings = "current snapshot", ifcJobs = "created in [from,to)", tickets = "created in [from,to)" }, accounts = await users.GroupBy(x => new { x.Role, x.IsActive }).Select(x => new { role = x.Key.Role.ToString(), x.Key.IsActive, count = x.Count() }).ToListAsync(ct), buildings = await buildings.GroupBy(x => x.IsActive).Select(x => new { x.Key, count = x.Count() }).ToListAsync(ct), ifcJobs = await jobs.GroupBy(x => x.Status).Select(x => new { status = x.Key.ToString(), count = x.Count() }).ToListAsync(ct), tickets = await tickets.GroupBy(x => x.Status).Select(x => new { status = x.Key.ToString(), count = x.Count() }).ToListAsync(ct) };
    }
    private Task<bool> IsAdmin(CancellationToken ct) => db.Users.AnyAsync(x => x.Id == User.GetActorId() && x.Role == UserRole.PlatformAdmin && x.IsActive && x.DeletedAt == null, ct);
    private static bool TryRange(DateTime? from, DateTime? to, out (DateTime From, DateTime To) range, out IActionResult? error)
    {
        range = (from ?? DateTime.UtcNow.AddDays(-30), to ?? DateTime.UtcNow);
        error = range.To <= range.From || range.To > range.From.AddDays(90)
            ? new ObjectResult(new ProblemDetails { Status = 400, Title = "Analytics range must be positive and at most 90 days.", Extensions = { ["code"] = "VALIDATION_ERROR" } }) { StatusCode = 400 }
            : null;
        return error is null;
    }
}
