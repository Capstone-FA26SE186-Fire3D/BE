using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.RejectScenarioVersion;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class ScenarioReviewStore(Fire3DDbContext db) : IScenarioReviewStore
{
    public async Task<AuthResult<Guid>> CreateRejectedReviewAsync(Guid actorId, Guid revisionId, Guid? organizationId,
        RejectScenarioVersionRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var revision = await db.Revisions.Include(r => r.Building).SingleOrDefaultAsync(r => r.Id == revisionId, ct);
        if (revision is null || !revision.Building.IsActive || revision.Building.DeletedAt is not null
            || organizationId.HasValue && revision.OrganizationId != organizationId.Value)
            return AuthResult<Guid>.Fail("NOT_FOUND", "Revision not found or access denied.", 404);

        var versionMatches = await db.ScenarioVersions.AnyAsync(v => v.Id == request.ScenarioVersionId
            && v.RevisionId == revisionId && v.BuildingId == revision.BuildingId
            && v.OrganizationId == revision.OrganizationId, ct);
        if (!versionMatches)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "Scenario version must belong to the specified revision.", 400);

        var validationMatches = await db.ValidationRuns.AnyAsync(v => v.Id == request.ValidationRunId
            && v.RevisionId == revisionId && v.ScenarioVersionId == request.ScenarioVersionId, ct);
        if (!validationMatches)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "Validation run must belong to the specified revision and scenario version.", 400);

        if (request.AnnotationSetId.HasValue && !await db.AnnotationSets.AnyAsync(a =>
                a.Id == request.AnnotationSetId.Value && a.RevisionId == revisionId, ct))
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "Annotation set must belong to the specified revision.", 400);

        if (await db.RevisionReviews.AnyAsync(r => r.ScenarioVersionId == request.ScenarioVersionId, ct))
            return AuthResult<Guid>.Fail("CONFLICT", "This scenario version already has a review.", 409);

        var reviewId = Guid.NewGuid();
        db.RevisionReviews.Add(new RevisionReview
        {
            Id = reviewId,
            RevisionId = revisionId,
            ScenarioVersionId = request.ScenarioVersionId,
            ValidationRunId = request.ValidationRunId,
            AnnotationSetId = request.AnnotationSetId,
            ReviewedBy = actorId,
            ReviewMessage = request.ReviewMessage,
            ReviewedAt = DateTime.UtcNow,
            Action = ReviewAction.Rejected
        });
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), UserId = actorId, OrganizationId = revision.OrganizationId, ActorType = "User",
            Action = AuditAction.Reject, TargetEntity = "ScenarioVersion", TargetId = request.ScenarioVersionId,
            CorrelationId = Guid.NewGuid(),
            NewValues = JsonSerializer.Serialize(new { action = "Rejected", revisionId, request.ScenarioVersionId, request.ValidationRunId }),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return AuthResult<Guid>.Ok(reviewId);
    }
}
