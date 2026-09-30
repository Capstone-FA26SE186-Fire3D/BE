using Fire3D.API.Authorization;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.API.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SupportController(Fire3DDbContext db) : ControllerBase
{
    public sealed record CreateFeedbackRequest(string Category, string Message, int? Rating = null);
    public sealed record CreateTicketRequest(string Subject, string Description, Guid? FeedbackId = null);

    [HttpPost("api/feedback")]
    public async Task<IActionResult> CreateFeedback(CreateFeedbackRequest request, CancellationToken ct)
    {
        var actor = await ActiveActor(ct); if (actor is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Category) || request.Category.Length > 100 || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 10_000 || request.Rating is < 1 or > 5) return Problem(statusCode: 400, title: "Feedback is invalid.", extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
        var now = DateTime.UtcNow; var item = new Feedback { Id = Guid.NewGuid(), SubmittedBy = actor.Id, OrganizationId = actor.OrganizationId, Category = request.Category.Trim(), Message = request.Message.Trim(), Rating = request.Rating, Status = FeedbackStatus.Submitted, CreatedAt = now, UpdatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct); db.Feedbacks.Add(item); Audit(actor, "feedback", item.Id, "Create", now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Created($"/api/feedback/{item.Id}", item);
    }

    [HttpGet("api/feedback")]
    public async Task<IActionResult> ListFeedback(CancellationToken ct)
    { var actor = await ActiveActor(ct); if (actor is null) return Unauthorized(); return Ok(await db.Feedbacks.AsNoTracking().Where(x => x.SubmittedBy == actor.Id).OrderByDescending(x => x.CreatedAt).ToListAsync(ct)); }

    [HttpPost("api/support/tickets")]
    public async Task<IActionResult> CreateTicket(CreateTicketRequest request, CancellationToken ct)
    {
        var actor = await ActiveActor(ct); if (actor is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 255 || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 10_000) return Problem(statusCode: 400, title: "Ticket is invalid.", extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });
        if (request.FeedbackId.HasValue && !await db.Feedbacks.AnyAsync(x => x.Id == request.FeedbackId && x.SubmittedBy == actor.Id, ct)) return Problem(statusCode: 404, title: "Feedback was not found.");
        var now = DateTime.UtcNow; var item = new SupportTicket { Id = Guid.NewGuid(), TicketNumber = $"SUP-{now:yyyyMMdd}-{Guid.NewGuid():N}"[..25], CreatedBy = actor.Id, OrganizationId = actor.OrganizationId, FeedbackId = request.FeedbackId, Subject = request.Subject.Trim(), Description = request.Description.Trim(), Status = SupportTicketStatus.Open, Priority = SupportPriority.Normal, CreatedAt = now, UpdatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct); db.SupportTickets.Add(item); Audit(actor, "support_tickets", item.Id, "Support", now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Created($"/api/support/tickets/{item.Id}", item);
    }

    [HttpGet("api/support/tickets")]
    public async Task<IActionResult> ListTickets(CancellationToken ct)
    { var actor = await ActiveActor(ct); if (actor is null) return Unauthorized(); return Ok(await db.SupportTickets.AsNoTracking().Where(x => x.CreatedBy == actor.Id).OrderByDescending(x => x.CreatedAt).ToListAsync(ct)); }

    [HttpGet("api/support/tickets/{id:guid}")]
    public async Task<IActionResult> GetTicket(Guid id, CancellationToken ct)
    { var actor = await ActiveActor(ct); if (actor is null) return Unauthorized(); var item = await db.SupportTickets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CreatedBy == actor.Id, ct); return item is null ? NotFound() : Ok(item); }

    private async Task<User?> ActiveActor(CancellationToken ct) => await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == User.GetActorId() && x.IsActive && x.DeletedAt == null, ct);
    private void Audit(User actor, string entity, Guid targetId, string action, DateTime now) => db.AuditLogs.Add(new() { Id = Guid.NewGuid(), UserId = actor.Id, OrganizationId = actor.OrganizationId, ActorType = "User", Action = Enum.Parse<AuditAction>(action), TargetEntity = entity, TargetId = targetId, CorrelationId = Guid.NewGuid(), CreatedAt = now });
}
