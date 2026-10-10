using Fire3D.Application.Authentication.Avatar;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Authentication;

public sealed class AvatarStore(Fire3DDbContext db) : IAvatarStore, IAvatarCleanupStore
{
    public async Task SaveUploadIntentAsync(AvatarUploadIntent intent, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.AvatarUploadIntents.Add(intent);
        await db.SaveChangesAsync(ct);
        await QueueInCurrentTransactionAsync(intent.StagingObjectKey, intent.ExpiresAt, ct);
        await transaction.CommitAsync(ct);
    }

    public Task<AvatarUploadIntent?> FindUploadIntentAsync(Guid id, Guid userId, CancellationToken ct) =>
        db.AvatarUploadIntents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    public async Task<AvatarCandidateReservation> ReserveCopyCandidateAsync(Guid intentId, Guid userId, long expectedProfileRevision,
        string sourceEtag, DateTime now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:avatar-intent:" + intentId}, 0))", ct);
        now = await DatabaseNowAsync(ct);
        var intent = await db.AvatarUploadIntents.SingleOrDefaultAsync(x => x.Id == intentId && x.UserId == userId, ct);
        if (intent is null || intent.CompletedAt.HasValue || intent.ExpiresAt <= now)
            return new(AvatarCandidateReservationStatus.Unavailable, null);
        if (intent.CandidateAttemptId.HasValue)
        {
            if (intent.CandidateLeaseUntil is null || intent.CandidateLeaseUntil <= now)
                return new(AvatarCandidateReservationStatus.Unavailable, null);
            if (intent.ExpectedProfileRevision != expectedProfileRevision || !string.Equals(intent.CandidateSourceEtag, sourceEtag, StringComparison.Ordinal))
                return new(AvatarCandidateReservationStatus.PreconditionFailed, null);
            await transaction.CommitAsync(ct);
            return new(AvatarCandidateReservationStatus.Reserved, new(intent.CandidateAttemptId.Value, intent.CandidateObjectKey!, intent.CandidateSourceEtag!, intent.CandidateLeaseUntil!.Value));
        }
        var candidate = new AvatarCopyCandidate(Guid.NewGuid(), $"avatars/users/{userId:N}/{Guid.NewGuid():N}", sourceEtag, now.AddMinutes(15));
        intent.ExpectedProfileRevision = expectedProfileRevision;
        intent.CandidateAttemptId = candidate.AttemptId;
        intent.CandidateObjectKey = candidate.ObjectKey;
        intent.CandidateSourceEtag = candidate.SourceEtag;
        intent.CandidateLeaseUntil = candidate.LeaseUntil;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AvatarCandidateReservationStatus.Reserved, candidate);
    }

    public async Task<AvatarFinalizeResult> FinalizeUploadAsync(Guid intentId, Guid userId, Guid attemptId, long expectedProfileRevision, string objectKey, DateTime completedAt, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await TakeIdentityReadLockAsync(userId, ct);
        completedAt = await DatabaseNowAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.IsActive && x.DeletedAt == null, ct);
        if (user is null || (user.OrganizationId is Guid org && !await db.Organizations.AnyAsync(x => x.Id == org && x.IsActive && x.DeletedAt == null, ct)))
            return new(AvatarFinalizeStatus.Unavailable, null);
        var previous = user.AvatarStorageKey;

        // Claiming the intent and advancing profile_revision are conditional writes in one
        // transaction. A stale request rolls back its claim, so an intent is consumed once.
        var claimed = await db.AvatarUploadIntents
            .Where(x => x.Id == intentId && x.UserId == userId && x.CompletedAt == null && x.ExpiresAt > completedAt
                && x.CandidateAttemptId == attemptId && x.CandidateObjectKey == objectKey && x.ExpectedProfileRevision == expectedProfileRevision
                && x.CandidateLeaseUntil > completedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CompletedAt, completedAt)
                .SetProperty(x => x.FinalObjectKey, objectKey), ct);
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
        if (!string.IsNullOrWhiteSpace(previous)) await QueueInCurrentTransactionAsync(previous, completedAt, ct);
        AddAudit(user, completedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AvatarFinalizeStatus.Finalized, previous);
    }

    public async Task<AvatarDeleteResult> DeleteAvatarAsync(Guid userId, long expectedProfileRevision, DateTime deletedAt, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await TakeIdentityReadLockAsync(userId, ct);
        deletedAt = await DatabaseNowAsync(ct);
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
        if (!string.IsNullOrWhiteSpace(previous)) await QueueInCurrentTransactionAsync(previous, deletedAt, ct);
        AddAudit(user, deletedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AvatarDeleteStatus.Deleted, previous);
    }

    public Task QueueAsync(string objectKey, DateTime availableAt, CancellationToken ct) => QueueInCurrentTransactionAsync(objectKey, availableAt, ct);

    public async Task<AvatarCleanupJob?> ClaimAsync(CancellationToken ct)
    {
        var result = await CleanupGateAsync("Claim", ct);
        return result.GetProperty("code").GetString() == "EMPTY" ? null : new(
            result.GetProperty("id").GetGuid(), result.GetProperty("objectKey").GetString()!,
            result.GetProperty("leaseToken").GetGuid(), result.GetProperty("attempt").GetInt32());
    }

    public async Task<bool> IsReferencedAsync(string objectKey, CancellationToken ct) =>
        (await CleanupGateAsync("Referenced", ct, objectKey)).GetProperty("referenced").GetBoolean();

    public async Task<bool> RenewAsync(AvatarCleanupJob job, CancellationToken ct) =>
        (await CleanupGateAsync("Renew", ct, id: job.Id, lease: job.LeaseToken)).GetProperty("code").GetString() == "OK";

    public async Task CompleteAsync(AvatarCleanupJob job, CancellationToken ct)
    {
        var result = await CleanupGateAsync("Complete", ct, id: job.Id, lease: job.LeaseToken);
        if (result.GetProperty("code").GetString() == "PROTECTED") await FailAsync(job, ct);
    }

    public async Task FailAsync(AvatarCleanupJob job, CancellationToken ct) =>
        _ = await CleanupGateAsync("Retry", ct, id: job.Id, lease: job.LeaseToken);

    private async Task QueueInCurrentTransactionAsync(string objectKey, DateTime now, CancellationToken ct) =>
        _ = await CleanupGateAsync("Enqueue", ct, objectKey, available: now);

    private async Task<System.Text.Json.JsonElement> CleanupGateAsync(string action, CancellationToken ct,
        string? key = null, Guid? id = null, Guid? lease = null, DateTime? available = null)
    {
        var json = await db.Database.SqlQuery<string>($"""
            SELECT public.avatar_cleanup_gate({action}::text,{key}::text,{id}::uuid,{lease}::uuid,{available}::timestamptz)::text AS "Value"
            """).SingleAsync(ct);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private void AddAudit(User user, DateTime now) => db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(), UserId = user.Id, OrganizationId = user.OrganizationId,
        ActorType = "User", Action = AuditAction.Update, TargetEntity = "users", TargetId = user.Id,
        CorrelationId = Guid.NewGuid(), CreatedAt = now
    });

    private async Task TakeIdentityReadLockAsync(Guid userId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management', 0))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:auth:" + userId}, 0))", ct);
    }

    // Transaction-start now() and caller timestamps can predate waiting for the lock.
    private Task<DateTime> DatabaseNowAsync(CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTime>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
}
