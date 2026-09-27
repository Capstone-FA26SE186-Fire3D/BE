using Fire3D.Application.Authentication.Avatar;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Authentication;

public sealed class AvatarStore(Fire3DDbContext db) : IAvatarStore
{
    public async Task SaveUploadIntentAsync(AvatarUploadIntent intent, CancellationToken ct)
    {
        db.AvatarUploadIntents.Add(intent);
        await db.SaveChangesAsync(ct);
    }

    public Task<AvatarUploadIntent?> FindUploadIntentAsync(Guid id, Guid userId, CancellationToken ct) =>
        db.AvatarUploadIntents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    public async Task<AvatarFinalizeResult> FinalizeUploadAsync(Guid intentId, Guid userId, long expectedProfileRevision, string objectKey, DateTime completedAt, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await TakeIdentityReadLockAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.IsActive && x.DeletedAt == null, ct);
        if (user is null || (user.OrganizationId is Guid org && !await db.Organizations.AnyAsync(x => x.Id == org && x.IsActive && x.DeletedAt == null, ct)))
            return new(AvatarFinalizeStatus.Unavailable, null);
        var previous = user.AvatarStorageKey;

        // Claiming the intent and advancing profile_revision are conditional writes in one
        // transaction. A stale request rolls back its claim, so an intent is consumed once.
        var claimed = await db.AvatarUploadIntents
            .Where(x => x.Id == intentId && x.UserId == userId && x.CompletedAt == null && x.ExpiresAt > completedAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CompletedAt, completedAt), ct);
        if (claimed != 1) return new(AvatarFinalizeStatus.Unavailable, null);

        var changed = await db.Users
            .Where(x => x.Id == userId && x.IsActive && x.DeletedAt == null && x.ProfileRevision == expectedProfileRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AvatarStorageKey, objectKey)
                .SetProperty(x => x.ProfileRevision, x => x.ProfileRevision + 1)
                .SetProperty(x => x.UpdatedAt, completedAt), ct);
        if (changed != 1)
        {
            await transaction.RollbackAsync(ct);
            return new(AvatarFinalizeStatus.PreconditionFailed, null);
        }
        user.AvatarStorageKey = objectKey;
        user.ProfileRevision = expectedProfileRevision + 1;
        AddAudit(user, completedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AvatarFinalizeStatus.Finalized, previous);
    }

    public async Task<AvatarDeleteResult> DeleteAvatarAsync(Guid userId, long expectedProfileRevision, DateTime deletedAt, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await TakeIdentityReadLockAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.IsActive && x.DeletedAt == null, ct);
        if (user is null || (user.OrganizationId is Guid org && !await db.Organizations.AnyAsync(x => x.Id == org && x.IsActive && x.DeletedAt == null, ct)))
            return new(AvatarDeleteStatus.Unavailable, null);
        var previous = user.AvatarStorageKey;
        var changed = await db.Users
            .Where(x => x.Id == userId && x.IsActive && x.DeletedAt == null && x.ProfileRevision == expectedProfileRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AvatarStorageKey, (string?)null)
                .SetProperty(x => x.ProfileRevision, x => x.ProfileRevision + 1)
                .SetProperty(x => x.UpdatedAt, deletedAt), ct);
        if (changed != 1)
        {
            await transaction.RollbackAsync(ct);
            return new(AvatarDeleteStatus.PreconditionFailed, null);
        }
        user.AvatarStorageKey = null;
        user.ProfileRevision = expectedProfileRevision + 1;
        AddAudit(user, deletedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AvatarDeleteStatus.Deleted, previous);
    }

    private void AddAudit(User user, DateTime now) => db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(), UserId = user.Id, OrganizationId = user.OrganizationId,
        ActorType = "User", Action = AuditAction.Update, TargetEntity = "users", TargetId = user.Id,
        CorrelationId = Guid.NewGuid(), CreatedAt = now
    });

    private Task TakeIdentityReadLockAsync(CancellationToken ct) => db.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management', 0))", ct);
}
