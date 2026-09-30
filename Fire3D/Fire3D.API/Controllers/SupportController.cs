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
    public sealed record MessageRequest(string Message);
    public sealed record AdminTicketUpdateRequest(SupportTicketStatus Status, SupportPriority Priority, Guid? AssignedTo);
    public sealed record AdminFeedbackUpdateRequest(FeedbackStatus Status);
    public sealed record TicketMessageResponse(Guid Id, Guid AuthorId, string Message, DateTime CreatedAt);
    public sealed record TicketDetailsResponse(Guid Id, string TicketNumber, string Subject, string Description,
        SupportTicketStatus Status, SupportPriority Priority, Guid? AssignedTo, DateTime? ResolvedAt, DateTime CreatedAt,
        DateTime UpdatedAt, IReadOnlyList<TicketMessageResponse> Messages);

    [HttpPost("api/feedback")]
    public async Task<IActionResult> CreateFeedback(CreateFeedbackRequest request, CancellationToken ct)
    {
        var actor = await SupportActor(ct); if (actor is null) return Forbid();
        if (!ValidFeedback(request)) return Invalid("Feedback is invalid.");
        var now = DateTime.UtcNow;
        var item = new Feedback { Id = Guid.NewGuid(), SubmittedBy = actor.Id, OrganizationId = actor.OrganizationId, Category = request.Category.Trim(), Message = request.Message.Trim(), Rating = request.Rating, Status = FeedbackStatus.Submitted, CreatedAt = now, UpdatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Feedbacks.Add(item); Audit(actor, "feedback", item.Id, AuditAction.Create, now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Created($"/api/feedback/{item.Id}", FeedbackView(item));
    }

    [HttpGet("api/feedback")]
    public async Task<IActionResult> ListFeedback(CancellationToken ct)
    {
        var actor = await SupportActor(ct); if (actor is null) return Forbid();
        var items = await db.Feedbacks.AsNoTracking().Where(x => x.SubmittedBy == actor.Id).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(items.Select(FeedbackView));
    }

    [HttpPost("api/support/tickets")]
    public async Task<IActionResult> CreateTicket(CreateTicketRequest request, CancellationToken ct)
    {
        var actor = await SupportActor(ct); if (actor is null) return Forbid();
        if (!ValidTicket(request)) return Invalid("Ticket is invalid.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (request.FeedbackId.HasValue && !await db.Feedbacks.AnyAsync(x => x.Id == request.FeedbackId && x.SubmittedBy == actor.Id, ct)) return Problem(statusCode: 404, title: "Feedback was not found.");
        var now = DateTime.UtcNow;
        var item = new SupportTicket { Id = Guid.NewGuid(), TicketNumber = $"SUP-{now:yyyyMMdd}-{Guid.NewGuid():N}"[..25], CreatedBy = actor.Id, OrganizationId = actor.OrganizationId, FeedbackId = request.FeedbackId, Subject = request.Subject.Trim(), Description = request.Description.Trim(), Status = SupportTicketStatus.Open, Priority = SupportPriority.Normal, CreatedAt = now, UpdatedAt = now };
        db.SupportTickets.Add(item); Audit(actor, "support_tickets", item.Id, AuditAction.Support, now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Created($"/api/support/tickets/{item.Id}", await Details(item, ct));
    }

    [HttpGet("api/support/tickets")]
    public async Task<IActionResult> ListTickets(CancellationToken ct)
    {
        var actor = await SupportActor(ct); if (actor is null) return Forbid();
        var items = await db.SupportTickets.AsNoTracking().Where(x => x.CreatedBy == actor.Id).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(items.Select(TicketSummary));
    }

    [HttpGet("api/support/tickets/{id:guid}")]
    public async Task<IActionResult> GetTicket(Guid id, CancellationToken ct)
    {
        var actor = await SupportActor(ct); if (actor is null) return Forbid();
        var item = await db.SupportTickets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.CreatedBy == actor.Id, ct);
        return item is null ? NotFound() : Ok(await Details(item, ct));
    }

    [HttpPost("api/support/tickets/{id:guid}/messages")]
    public async Task<IActionResult> AddMessage(Guid id, MessageRequest request, CancellationToken ct)
    {
        var actor = await SupportActor(ct); if (actor is null) return Forbid();
        return await AddMessageCore(id, request, actor, true, ct);
    }

    [HttpGet("api/admin/feedback")]
    public async Task<IActionResult> AdminFeedback(CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        return Ok((await db.Feedbacks.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct)).Select(FeedbackView));
    }

    [HttpPatch("api/admin/feedback/{id:guid}/status")]
    public async Task<IActionResult> AdminUpdateFeedback(Guid id, AdminFeedbackUpdateRequest request, CancellationToken ct)
    {
        var admin = await AdminActor(ct); if (admin is null) return Forbid();
        if (!Enum.IsDefined(request.Status)) return Invalid("Feedback status is invalid.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock("feedback", id, ct);
        var item = await db.Feedbacks.SingleOrDefaultAsync(x => x.Id == id, ct); if (item is null) return NotFound();
        if (!CanTransition(item.Status, request.Status)) return Conflict("Feedback status transition is invalid.");
        var now = DateTime.UtcNow; item.Status = request.Status; item.ReviewedBy = request.Status is FeedbackStatus.Reviewed or FeedbackStatus.Closed ? admin.Id : item.ReviewedBy; item.ReviewedAt = request.Status is FeedbackStatus.Reviewed or FeedbackStatus.Closed ? now : item.ReviewedAt; item.UpdatedAt = now;
        Audit(admin, "feedback", item.Id, AuditAction.Update, now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Ok(FeedbackView(item));
    }

    [HttpGet("api/admin/support/tickets")]
    public async Task<IActionResult> AdminTickets(CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        return Ok((await db.SupportTickets.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct)).Select(TicketSummary));
    }

    [HttpGet("api/admin/support/tickets/{id:guid}")]
    public async Task<IActionResult> AdminTicket(Guid id, CancellationToken ct)
    {
        if (!await IsAdmin(ct)) return Forbid();
        var item = await db.SupportTickets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); return item is null ? NotFound() : Ok(await Details(item, ct));
    }

    [HttpPost("api/admin/support/tickets/{id:guid}/messages")]
    public async Task<IActionResult> AdminMessage(Guid id, MessageRequest request, CancellationToken ct)
    {
        var admin = await AdminActor(ct); if (admin is null) return Forbid();
        return await AddMessageCore(id, request, admin, false, ct);
    }

    [HttpPatch("api/admin/support/tickets/{id:guid}")]
    public async Task<IActionResult> AdminUpdate(Guid id, AdminTicketUpdateRequest request, CancellationToken ct)
    {
        var admin = await AdminActor(ct); if (admin is null) return Forbid();
        if (!Enum.IsDefined(request.Status) || !Enum.IsDefined(request.Priority)) return Invalid("Ticket status or priority is invalid.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock("ticket", id, ct);
        var ticket = await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == id, ct); if (ticket is null) return NotFound();
        if (!CanTransition(ticket.Status, request.Status)) return Conflict("Ticket status transition is invalid.");
        if (request.AssignedTo.HasValue && !await db.Users.AnyAsync(x => x.Id == request.AssignedTo && x.Role == UserRole.PlatformAdmin && x.IsActive && !x.DeletedAt.HasValue, ct)) return Invalid("Assignee is invalid.");
        var now = DateTime.UtcNow; ticket.Status = request.Status; ticket.Priority = request.Priority; ticket.AssignedTo = request.AssignedTo; ticket.ResolvedAt = request.Status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed ? ticket.ResolvedAt ?? now : null; ticket.UpdatedAt = now;
        Audit(admin, "support_tickets", ticket.Id, AuditAction.Update, now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Ok(await Details(ticket, ct));
    }

    private async Task<IActionResult> AddMessageCore(Guid id, MessageRequest request, User actor, bool ownerOnly, CancellationToken ct)
    {
        if (!ValidMessage(request)) return Invalid("Message is invalid.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock("ticket", id, ct);
        var ticket = await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == id && (!ownerOnly || x.CreatedBy == actor.Id), ct);
        if (ticket is null) return NotFound();
        if (ticket.Status == SupportTicketStatus.Closed) return Conflict("Ticket is closed.");
        var now = DateTime.UtcNow;
        var message = new SupportTicketMessage { Id = Guid.NewGuid(), TicketId = id, AuthorId = actor.Id, Message = request.Message.Trim(), CreatedAt = now };
        db.SupportTicketMessages.Add(message); ticket.UpdatedAt = now; Audit(actor, "support_ticket_messages", message.Id, AuditAction.Support, now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Created($"/api/support/tickets/{id}", new TicketMessageResponse(message.Id, message.AuthorId, message.Message, message.CreatedAt));
    }

    private async Task<TicketDetailsResponse> Details(SupportTicket ticket, CancellationToken ct) => new(ticket.Id, ticket.TicketNumber, ticket.Subject, ticket.Description, ticket.Status, ticket.Priority, ticket.AssignedTo, ticket.ResolvedAt, ticket.CreatedAt, ticket.UpdatedAt,
        await db.SupportTicketMessages.AsNoTracking().Where(x => x.TicketId == ticket.Id).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => new TicketMessageResponse(x.Id, x.AuthorId, x.Message, x.CreatedAt)).ToListAsync(ct));
    private static object TicketSummary(SupportTicket x) => new { x.Id, x.TicketNumber, x.Subject, x.Status, x.Priority, x.AssignedTo, x.ResolvedAt, x.CreatedAt, x.UpdatedAt };
    private static object FeedbackView(Feedback x) => new { x.Id, x.Category, x.Message, x.Rating, x.Status, x.ReviewedBy, x.ReviewedAt, x.CreatedAt, x.UpdatedAt };
    private static bool ValidFeedback(CreateFeedbackRequest r) => !string.IsNullOrWhiteSpace(r.Category) && r.Category.Length <= 100 && !string.IsNullOrWhiteSpace(r.Message) && r.Message.Length <= 10_000 && r.Rating is not (< 1 or > 5);
    private static bool ValidTicket(CreateTicketRequest r) => !string.IsNullOrWhiteSpace(r.Subject) && r.Subject.Length <= 255 && !string.IsNullOrWhiteSpace(r.Description) && r.Description.Length <= 10_000;
    private static bool ValidMessage(MessageRequest r) => !string.IsNullOrWhiteSpace(r.Message) && r.Message.Length <= 10_000;
    private static bool CanTransition(SupportTicketStatus from, SupportTicketStatus to) => from == to || (from, to) is (SupportTicketStatus.Open, SupportTicketStatus.InProgress) or (SupportTicketStatus.InProgress, SupportTicketStatus.Resolved) or (SupportTicketStatus.Resolved, SupportTicketStatus.Closed) or (SupportTicketStatus.Resolved, SupportTicketStatus.Open) or (SupportTicketStatus.Closed, SupportTicketStatus.Open);
    private static bool CanTransition(FeedbackStatus from, FeedbackStatus to) => from == to || (from, to) is (FeedbackStatus.Submitted, FeedbackStatus.Reviewed) or (FeedbackStatus.Reviewed, FeedbackStatus.Closed);
    private static IActionResult Invalid(string title) => new ObjectResult(new ProblemDetails { Status = 400, Title = title, Extensions = { ["code"] = "VALIDATION_ERROR" } }) { StatusCode = 400 };
    private static IActionResult Conflict(string title) => new ObjectResult(new ProblemDetails { Status = 409, Title = title, Extensions = { ["code"] = "INVALID_STATUS_TRANSITION" } }) { StatusCode = 409 };
    private async Task<User?> SupportActor(CancellationToken ct) { var actor = await ActiveActor(ct); return actor?.Role is UserRole.Trainee or UserRole.OrganizationUser ? actor : null; }
    private async Task<User?> AdminActor(CancellationToken ct) { var actor = await ActiveActor(ct); return actor?.Role == UserRole.PlatformAdmin ? actor : null; }
    private async Task<bool> IsAdmin(CancellationToken ct) => await AdminActor(ct) is not null;
    private async Task<User?> ActiveActor(CancellationToken ct) => await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == User.GetActorId() && x.IsActive && x.DeletedAt == null, ct);
    private Task Lock(string type, Guid id, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:support:" + type + ":" + id}, 0))", ct);
    private void Audit(User actor, string entity, Guid targetId, AuditAction action, DateTime now) => db.AuditLogs.Add(new() { Id = Guid.NewGuid(), UserId = actor.Id, OrganizationId = actor.OrganizationId, ActorType = "User", Action = action, TargetEntity = entity, TargetId = targetId, CorrelationId = Guid.NewGuid(), CreatedAt = now });
}
