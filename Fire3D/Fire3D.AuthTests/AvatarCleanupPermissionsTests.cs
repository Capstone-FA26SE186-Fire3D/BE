using Fire3D.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AvatarCleanupPermissionsTests
{
    [BillingPostgresFact]
    public async Task Temporary_tables_cannot_hide_protected_objects_from_definer_gates()
    {
        await using var db = await BillingDatabase.Create(migrationHistory: true);
        const string key = "avatars/users/actual-protected";
        await db.Sql($"UPDATE users SET avatar_storage_key='{key}' WHERE id='{BillingDatabase.Owner}'");
        Assert.Equal(3L, await db.Scalar("""
            SELECT count(*) FROM pg_proc WHERE proname IN ('cleanup_pending_registrations','avatar_cleanup_gate','scenario_readiness_gate')
             AND prosecdef AND array_to_string(proconfig,',') ~ 'pg_catalog, *public, *pg_temp$'
            """));
        await using var context = db.Context();
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api; CREATE TEMP TABLE users (LIKE public.users)");
        var cleanup = new AvatarStore(context);
        Assert.True(await cleanup.IsReferencedAsync(key, default));
        await cleanup.QueueAsync(key, DateTime.UtcNow.AddMinutes(-1), default);
        var job = (await cleanup.ClaimAsync(default))!;
        await cleanup.CompleteAsync(job, default);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM avatar_object_cleanups"));
    }

    [BillingPostgresFact]
    public async Task Actual_migrations_allow_cleanup_under_rls_without_direct_queue_dml()
    {
        await using var db = await BillingDatabase.Create(migrationHistory: true);
        const string key = "avatars/users/protected";
        await db.Sql($"UPDATE users SET avatar_storage_key='{key}' WHERE id='{BillingDatabase.Owner}'");
        await using var context = db.Context();
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
        var cleanup = new AvatarStore(context);
        await cleanup.QueueAsync(key, DateTime.UtcNow.AddMinutes(-1), default);
        var job = (await cleanup.ClaimAsync(default))!;
        Assert.True(await cleanup.IsReferencedAsync(key, default));
        Assert.True(await cleanup.RenewAsync(job, default));
        await cleanup.CompleteAsync(job, default);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM avatar_object_cleanups"));
        Assert.False((bool)(await db.Scalar("SELECT has_table_privilege('fire3d_api','avatar_object_cleanups','INSERT,UPDATE,DELETE')"))!);
        Assert.False((bool)(await db.Scalar("SELECT has_function_privilege('authenticated','public.avatar_cleanup_gate(text,text,uuid,uuid,timestamptz)','EXECUTE')"))!);
        await db.Sql($"UPDATE users SET avatar_storage_key=NULL WHERE id='{BillingDatabase.Owner}'; UPDATE avatar_object_cleanups SET available_at=now()-interval '1 minute'");
        var current = (await cleanup.ClaimAsync(default))!;
        await cleanup.CompleteAsync(job, default);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM avatar_object_cleanups"));
        await cleanup.CompleteAsync(current, default);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM avatar_object_cleanups"));
    }

    [BillingPostgresFact]
    public async Task Expired_lease_cannot_renew_or_ack_and_late_upload_grace_is_protected()
    {
        await using var db = await BillingDatabase.Create(migrationHistory: true);
        await db.Sql($$"""
            INSERT INTO avatar_upload_intents(id,user_id,content_type,expected_size_bytes,staging_object_key,created_at,expires_at)
            VALUES(gen_random_uuid(),'{{BillingDatabase.Owner}}','image/png',100,'avatars/staging/late',now()-interval '1 hour',now()-interval '1 minute');
            """);
        await using var context = db.Context(); await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
        var cleanup = new AvatarStore(context);
        await cleanup.QueueAsync("avatars/staging/late", DateTime.UtcNow.AddMinutes(-1), default);
        var job = (await cleanup.ClaimAsync(default))!;
        Assert.True(await cleanup.IsReferencedAsync(job.ObjectKey, default));
        await db.Sql("UPDATE avatar_object_cleanups SET lease_until=now()-interval '1 second'");
        Assert.False(await cleanup.RenewAsync(job, default));
        await cleanup.CompleteAsync(job, default); await cleanup.FailAsync(job, default);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM avatar_object_cleanups WHERE lease_token IS NOT NULL"));
    }
}
