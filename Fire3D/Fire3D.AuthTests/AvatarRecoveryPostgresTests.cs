using Fire3D.Application.Authentication.Avatar;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AvatarRecoveryPostgresTests
{
    [BillingPostgresFact]
    public async Task Restricted_runtime_adopts_owned_candidate_and_keeps_current_object_protected()
    {
        await using var db = await BillingDatabase.Create(false);
        await db.Sql("""
            DO $$ BEGIN
              IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
                CREATE ROLE fire3d_api NOLOGIN NOSUPERUSER NOBYPASSRLS;
              END IF;
            END $$;
            GRANT USAGE ON SCHEMA public TO fire3d_api;
            GRANT SELECT,INSERT,UPDATE ON users,organizations,avatar_upload_intents TO fire3d_api;
            GRANT SELECT,INSERT,UPDATE,DELETE ON avatar_object_cleanups TO fire3d_api;
            GRANT INSERT ON audit_logs TO fire3d_api;
            """);
        await BackendDatabasePermissionsTests.Apply(db, "AddAvatarCleanupRecovery");
        await BackendDatabasePermissionsTests.Apply(db, "AddAvatarCopyCandidates");
        await BackendDatabasePermissionsTests.Apply(db, "HardenBackendObjectPermissions");
        var intent = await Intent(db);
        await using var context = db.Context();
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
        var store = new AvatarStore(context);
        Assert.Null(await store.FindUploadIntentAsync(intent.Id, BillingDatabase.Other, default));
        var candidate = (await store.ReserveCopyCandidateAsync(intent.Id, intent.UserId, 1, "etag", DateTime.UtcNow, default)).Candidate!;
        Assert.True((await store.FinalizeUploadAsync(intent.Id, intent.UserId, candidate.AttemptId, 1, candidate.ObjectKey, DateTime.UtcNow, default)).Finalized);
        Assert.True(await store.IsReferencedAsync(candidate.ObjectKey, default));
        Assert.True((await store.DeleteAvatarAsync(intent.UserId, 2, DateTime.UtcNow, default)).Deleted);
        Assert.False(await store.IsReferencedAsync(candidate.ObjectKey, default));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM avatar_object_cleanups WHERE object_key='{candidate.ObjectKey}'"));
    }

    private static async Task<AvatarUploadIntent> Intent(BillingDatabase db)
    {
        var intent = new AvatarUploadIntent
        {
            Id = Guid.NewGuid(), UserId = BillingDatabase.Owner, ContentType = "image/png",
            ExpectedSizeBytes = 100, StagingObjectKey = "avatars/staging/" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        await using var context = db.Context();
        await new AvatarStore(context).SaveUploadIntentAsync(intent, default);
        return intent;
    }

    [BillingPostgresFact]
    public async Task Expired_candidate_cannot_be_reserved_again()
    {
        await using var db = await BillingDatabase.Create(false);
        var intent = await Intent(db);
        await using var first = db.Context();
        var candidate = (await new AvatarStore(first).ReserveCopyCandidateAsync(intent.Id, intent.UserId, 1, "etag", DateTime.UtcNow, default)).Candidate!;
        await db.Sql($"UPDATE avatar_upload_intents SET candidate_lease_until=now()-interval '1 second' WHERE id='{intent.Id}'");
        await using var retry = db.Context();
        Assert.Equal(AvatarCandidateReservationStatus.Unavailable,
            (await new AvatarStore(retry).ReserveCopyCandidateAsync(intent.Id, intent.UserId, 1, "etag", DateTime.UtcNow, default)).Status);
    }

    [BillingPostgresFact]
    public async Task Expired_candidate_cannot_be_adopted_even_with_old_request_timestamp()
    {
        await using var db = await BillingDatabase.Create(false);
        var intent = await Intent(db);
        await using var first = db.Context();
        var candidate = (await new AvatarStore(first).ReserveCopyCandidateAsync(intent.Id, intent.UserId, 1, "etag", DateTime.UtcNow, default)).Candidate!;
        await db.Sql($"UPDATE avatar_upload_intents SET candidate_lease_until=now()-interval '1 second' WHERE id='{intent.Id}'");
        await using var finish = db.Context();
        var result = await new AvatarStore(finish).FinalizeUploadAsync(intent.Id, intent.UserId, candidate.AttemptId, 1, candidate.ObjectKey, DateTime.UtcNow.AddMinutes(-1), default);
        Assert.Equal(AvatarFinalizeStatus.Unavailable, result.Status);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM users WHERE avatar_storage_key IS NOT NULL"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Audit_failure_rolls_back_adoption_and_candidate_remains_recoverable()
    {
        await using var db = await BillingDatabase.Create(false);
        var intent = await Intent(db);
        await using var reserve = db.Context();
        var candidate = (await new AvatarStore(reserve).ReserveCopyCandidateAsync(intent.Id, intent.UserId, 1, "etag", DateTime.UtcNow, default)).Candidate!;
        await db.Sql("CREATE FUNCTION fail_avatar_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit unavailable'; END $$; CREATE TRIGGER fail_avatar_audit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_avatar_audit()");
        await using var finish = db.Context();
        await Assert.ThrowsAnyAsync<Exception>(() => new AvatarStore(finish).FinalizeUploadAsync(intent.Id, intent.UserId, candidate.AttemptId, 1, candidate.ObjectKey, DateTime.UtcNow, default));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM users WHERE avatar_storage_key IS NOT NULL"));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM avatar_upload_intents WHERE id='{intent.Id}' AND completed_at IS NULL AND candidate_object_key='{candidate.ObjectKey}'"));
        await db.Sql($"UPDATE avatar_upload_intents SET candidate_lease_until=now()-interval '2 hours' WHERE id='{intent.Id}'; UPDATE avatar_object_cleanups SET available_at=now()+interval '1 day'");
        await using var recovery = db.Context();
        var cleanup = new AvatarStore(recovery);
        var job = await cleanup.ClaimAsync(default);
        Assert.Equal(candidate.ObjectKey, job!.ObjectKey);
        Assert.False(await cleanup.IsReferencedAsync(job.ObjectKey, default));
        await cleanup.FailAsync(job, default);
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM avatar_object_cleanups WHERE object_key='{candidate.ObjectKey}' AND lease_token IS NULL"));
    }

    [BillingPostgresFact]
    public async Task Complete_and_delete_compete_on_one_profile_revision_without_losing_cleanup()
    {
        await using var db = await BillingDatabase.Create(false);
        var intent = await Intent(db);
        await db.Sql($"UPDATE users SET avatar_storage_key='avatars/users/old' WHERE id='{intent.UserId}'");
        await using var reserve = db.Context();
        var candidate = (await new AvatarStore(reserve).ReserveCopyCandidateAsync(intent.Id, intent.UserId, 1, "etag", DateTime.UtcNow, default)).Candidate!;
        async Task<bool> Complete()
        {
            await using var context = db.Context();
            return (await new AvatarStore(context).FinalizeUploadAsync(intent.Id, intent.UserId, candidate.AttemptId, 1, candidate.ObjectKey, DateTime.UtcNow, default)).Finalized;
        }
        async Task<bool> Delete()
        {
            await using var context = db.Context();
            return (await new AvatarStore(context).DeleteAvatarAsync(intent.UserId, 1, DateTime.UtcNow, default)).Deleted;
        }
        Assert.Single(await Task.WhenAll(Complete(), Delete()), x => x);
        Assert.Equal(2L, await db.Scalar($"SELECT profile_revision FROM users WHERE id='{intent.UserId}'"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM avatar_object_cleanups WHERE object_key='avatars/users/old'"));
    }

    [BillingPostgresFact]
    public async Task Cleanup_retains_protected_object_and_fences_old_worker_receipt()
    {
        await using var db = await BillingDatabase.Create(false);
        await db.Sql($"UPDATE users SET avatar_storage_key='avatars/users/current' WHERE id='{BillingDatabase.Owner}'");
        await using var context = db.Context();
        var cleanup = new AvatarStore(context);
        await cleanup.QueueAsync("avatars/users/current", DateTime.UtcNow.AddSeconds(-1), default);
        var old = (await cleanup.ClaimAsync(default))!;
        Assert.True(await cleanup.IsReferencedAsync(old.ObjectKey, default));
        await cleanup.FailAsync(old, default);
        await db.Sql("UPDATE avatar_object_cleanups SET available_at=now()-interval '1 second'");
        var current = (await cleanup.ClaimAsync(default))!;
        Assert.NotEqual(old.LeaseToken, current.LeaseToken);
        await cleanup.CompleteAsync(old, default);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM avatar_object_cleanups"));
        await cleanup.FailAsync(current, default);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM avatar_object_cleanups WHERE lease_token IS NULL"));
    }
}
