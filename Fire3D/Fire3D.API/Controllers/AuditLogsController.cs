using Fire3D.API.Authorization;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/admin/audit-logs")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuditLogsController(Fire3DDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid? actorId, Guid? organizationId, string? action, Guid? targetId, Guid? correlationId,
        DateTime? from, DateTime? to, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (!await IsAdmin(ct)) return Forbid();
        if (page is < 1 or > 100000 || pageSize is < 1 or > 100 || (from.HasValue && to.HasValue && (to <= from || to > from.Value.AddDays(90)))) return Problem(statusCode: 400, title: "Invalid audit filters.", extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
        var lower = from ?? DateTime.UtcNow.AddDays(-30); var upper = to ?? DateTime.UtcNow;
        var query = db.AuditLogs.AsNoTracking().Where(x => x.CreatedAt >= lower && x.CreatedAt < upper);
        if (actorId.HasValue) query = query.Where(x => x.UserId == actorId); if (organizationId.HasValue) query = query.Where(x => x.OrganizationId == organizationId); if (targetId.HasValue) query = query.Where(x => x.TargetId == targetId); if (correlationId.HasValue) query = query.Where(x => x.CorrelationId == correlationId); if (Enum.TryParse<AuditAction>(action, true, out var parsed)) query = query.Where(x => x.Action == parsed);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).Select(x => new { x.Id, x.UserId, x.OrganizationId, Action = x.Action.ToString(), x.TargetEntity, x.TargetId, x.CorrelationId, x.CreatedAt }).ToListAsync(ct);
        return Ok(new { items, total, page, pageSize, from = lower, to = upper });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    { if (!await IsAdmin(ct)) return Forbid(); var item = await db.AuditLogs.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.UserId, x.OrganizationId, Action = x.Action.ToString(), x.TargetEntity, x.TargetId, x.CorrelationId, x.CreatedAt }).SingleOrDefaultAsync(ct); return item is null ? NotFound() : Ok(item); }

    private Task<bool> IsAdmin(CancellationToken ct) => db.Users.AnyAsync(x => x.Id == User.GetActorId() && x.Role == UserRole.PlatformAdmin && x.IsActive && x.DeletedAt == null, ct);
}
