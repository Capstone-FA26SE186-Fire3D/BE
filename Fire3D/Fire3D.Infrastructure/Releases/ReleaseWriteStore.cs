using Fire3D.Application.Authentication;
using Fire3D.Application.Releases.Commands.PublishRelease;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Releases;

public sealed class ReleaseWriteStore(Fire3DDbContext db) : IReleaseWriteStore
{
    public async Task<AuthResult<bool>> PublishReleaseAsync(Guid actorId, Guid releaseId, Guid organizationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var release = await db.Releases.FirstOrDefaultAsync(r => r.Id == releaseId, ct);
        if (release == null || (organizationId != Guid.Empty && release.OrganizationId != organizationId))
            return AuthResult<bool>.Fail("NOT_FOUND", "Release not found or access denied.", 404);

        if (release.Status != Fire3D.Domain.Enums.ReleaseStatus.Built)
            return AuthResult<bool>.Fail("INVALID_STATE", "Release must be in Built state to be published.", 400);

        release.Status = Fire3D.Domain.Enums.ReleaseStatus.Published;
        release.PublishedAt = DateTime.UtcNow;
        release.PublishedBy = actorId;
        release.UpdatedAt = DateTime.UtcNow;

        var audit = new Fire3D.Domain.Entities.AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = actorId,
            OrganizationId = release.OrganizationId,
            ActorType = "User",
            Action = Fire3D.Domain.Enums.AuditAction.Publish,
            TargetEntity = "Release",
            TargetId = releaseId,
            CorrelationId = Guid.NewGuid(),
            NewValues = $$"""{"status": "Published"}""",
            OldValues = $$"""{"status": "Built"}""",
            CreatedAt = DateTime.UtcNow
        };
        db.AuditLogs.Add(audit);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        
        return AuthResult<bool>.Ok(true);
    }
}
