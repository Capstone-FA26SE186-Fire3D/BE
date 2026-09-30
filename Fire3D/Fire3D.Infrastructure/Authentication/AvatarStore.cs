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
        var intent = await db.AvatarUploadIntents.SingleOrDefaultAsync(x => x.Id == intentId && x.UserId == userId, ct);
        if (intent is null || intent.CompletedAt.HasValue || intent.ExpiresAt <= now)
            return new(AvatarCandidateReservationStatus.Unavailable, null);
        if (intent.CandidateAttemptId.HasValue)
        {
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
        await TakeIdentityReadLockAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.IsActive && x.DeletedAt == null, ct);
        if (user is null || (user.OrganizationId is Guid org && !await db.Organizations.AnyAsync(x => x.Id == org && x.IsActive && x.DeletedAt == null, ct)))
            return new(AvatarFinalizeStatus.Unavailable, null);
        var previous = user.AvatarStorageKey;

        // Claiming the intent and advancing profile_revision are conditional writes in one
        // transaction. A stale request rolls back its claim, so an intent is consumed once.
        var claimed = await db.AvatarUploadIntents
            .Where(x => x.Id == intentId && x.UserId == userId && x.CompletedAt == null && x.ExpiresAt > completedAt
                && x.CandidateAttemptId == attemptId && x.CandidateObjectKey == objectKey && x.ExpectedProfileRevision == expectedProfileRevision)
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
        if (!string.IsNullOrWhiteSpace(previous)) await QueueInCurrentTransactionAsync(previous, deletedAt, ct);
        AddAudit(user, deletedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AvatarDeleteStatus.Deleted, previous);
    }

    public Task QueueAsync(string objectKey, DateTime availableAt, CancellationToken ct) => QueueInCurrentTransactionAsync(objectKey, availableAt, ct);

    public async Task<AvatarCleanupJob?> ClaimAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO public.avatar_object_cleanups(id,object_key,available_at,attempts,created_at)
            SELECT gen_random_uuid(), candidate_object_key, now(), 0, now()
            FROM public.avatar_upload_intents
            WHERE completed_at IS NULL AND candidate_object_key IS NOT NULL
              AND candidate_lease_until <= now() - interval '1 hour'
            ON CONFLICT (object_key) DO NOTHING;
            """, ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            WITH candidate AS (
                SELECT id FROM public.avatar_object_cleanups
                WHERE available_at<=now() AND (lease_until IS NULL OR lease_until<=now())
                ORDER BY available_at,created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE public.avatar_object_cleanups cleanup
            SET lease_token=@lease,lease_until=now()+interval '2 minutes',attempts=attempts+1
            FROM candidate WHERE cleanup.id=candidate.id
            RETURNING cleanup.id,cleanup.object_key,cleanup.lease_token,cleanup.attempts
            """, (Npgsql.NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("lease", Guid.NewGuid());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt32(3)) : null;
    }

    public Task<bool> IsReferencedAsync(string objectKey, CancellationToken ct) => db.Database.SqlQuery<bool>($"""
        SELECT EXISTS(
            SELECT 1 FROM public.users WHERE avatar_storage_key={objectKey}
            UNION ALL SELECT 1 FROM public.avatar_upload_intents
                WHERE staging_object_key={objectKey} AND completed_at IS NULL AND expires_at > now()
            UNION ALL SELECT 1 FROM public.avatar_upload_intents
                WHERE candidate_object_key={objectKey} AND completed_at IS NULL
                  AND candidate_lease_until > now() - interval '1 hour') AS "Value"
        """).SingleAsync(ct);

    public Task CompleteAsync(AvatarCleanupJob job, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
        DELETE FROM public.avatar_object_cleanups WHERE id={job.Id} AND lease_token={job.LeaseToken}
        """, ct);

    public Task FailAsync(AvatarCleanupJob job, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(900, Math.Pow(2, job.Attempt) * 15));
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.avatar_object_cleanups SET available_at=now()+{delay},lease_token=NULL,lease_until=NULL
            WHERE id={job.Id} AND lease_token={job.LeaseToken}
            """, ct);
    }

    private Task QueueInCurrentTransactionAsync(string objectKey, DateTime now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.avatar_object_cleanups(id,object_key,available_at,attempts,created_at)
            VALUES ({Guid.NewGuid()},{objectKey},{now},0,{now}) ON CONFLICT (object_key) DO NOTHING
            """, ct);

    private void AddAudit(User user, DateTime now) => db.AuditLogs.Add(new AuditLog
    {
        Id = Guid.NewGuid(), UserId = user.Id, OrganizationId = user.OrganizationId,
        ActorType = "User", Action = AuditAction.Update, TargetEntity = "users", TargetId = user.Id,
        CorrelationId = Guid.NewGuid(), CreatedAt = now
    });

    private Task TakeIdentityReadLockAsync(CancellationToken ct) => db.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management', 0))", ct);
}
