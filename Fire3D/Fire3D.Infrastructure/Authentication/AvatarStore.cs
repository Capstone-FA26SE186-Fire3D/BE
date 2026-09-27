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

    public async Task<AvatarFinalizeResult> FinalizeUploadAsync(Guid intentId, Guid userId, string objectKey, DateTime completedAt, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var intent = await db.AvatarUploadIntents.SingleOrDefaultAsync(x => x.Id == intentId && x.UserId == userId && x.CompletedAt == null && x.ExpiresAt > completedAt, ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.IsActive && x.DeletedAt == null, ct);
        if (intent is null || user is null || (user.OrganizationId is Guid org && !await db.Organizations.AnyAsync(x => x.Id == org && x.IsActive && x.DeletedAt == null, ct)))
            return new(false, null);
        var previous = user.AvatarStorageKey;
        user.AvatarStorageKey = objectKey;
        user.ProfileRevision++;
        user.UpdatedAt = completedAt;
        intent.CompletedAt = completedAt;
        AddAudit(user, completedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(true, previous);
    }

    public async Task<AvatarDeleteResult> DeleteAvatarAsync(Guid userId, DateTime deletedAt, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.IsActive && x.DeletedAt == null, ct);
        if (user is null || (user.OrganizationId is Guid org && !await db.Organizations.AnyAsync(x => x.Id == org && x.IsActive && x.DeletedAt == null, ct)))
            return new(false, null);
        var previous = user.AvatarStorageKey;
        if (previous is null) { await transaction.CommitAsync(ct); return new(true, null); }
        user.AvatarStorageKey = null;
        user.ProfileRevision++;
        user.UpdatedAt = deletedAt;
        AddAudit(user, deletedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(true, previous);
    }

    private void AddAudit(User user, DateTime now) => db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(), UserId = user.Id, OrganizationId = user.OrganizationId,
        ActorType = "User", Action = AuditAction.Update, TargetEntity = "users", TargetId = user.Id,
        CorrelationId = Guid.NewGuid(), CreatedAt = now
    });
}
