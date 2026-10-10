using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class PendingCleanupPermissionsTests
{
    [BillingPostgresFact]
    public async Task Actual_migrations_allow_bounded_cleanup_without_runtime_delete_and_preserve_history()
    {
        await using var db = await BillingDatabase.Create(migrationHistory: true);
        var expired = Guid.NewGuid(); var historical = Guid.NewGuid(); var verified = Guid.NewGuid();
        await db.Sql($$"""
            INSERT INTO users(id,email,username,role,registration_expires_at,email_verified_at) VALUES
             ('{{expired}}','expired@example.test','expired_cleanup','Trainee',now()-interval '1 minute',NULL),
             ('{{historical}}','history@example.test','history_cleanup','Trainee',now()-interval '1 minute',NULL),
             ('{{verified}}','verified@example.test','verified_cleanup','Trainee',now()-interval '1 minute',now());
            INSERT INTO audit_logs(id,user_id,actor_type,action,target_entity,created_at)
             VALUES(gen_random_uuid(),'{{historical}}','User','Create','users',now());
            """);
        await using var context = db.Context();
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT public.cleanup_pending_registrations(100) AS \"Value\"").SingleAsync());
        Assert.False((bool)(await db.Scalar("SELECT has_table_privilege('fire3d_api','users','DELETE')"))!);
        Assert.Equal(0L, await db.Scalar($"SELECT count(*) FROM users WHERE id='{expired}'"));
        Assert.Equal(2L, await db.Scalar($"SELECT count(*) FROM users WHERE id IN ('{historical}','{verified}')"));
        Assert.False((bool)(await db.Scalar("SELECT has_function_privilege('anon','public.cleanup_pending_registrations(integer)','EXECUTE')"))!);
    }

    [BillingPostgresFact]
    public async Task Verification_lock_skips_cleanup_then_verified_user_is_retained()
    {
        await using var db = await BillingDatabase.Create(migrationHistory: true);
        var id = Guid.NewGuid();
        await db.Sql($"INSERT INTO users(id,email,username,role,registration_expires_at) VALUES('{id}','lock@example.test','locked_cleanup','Trainee',now()-interval '1 minute')");
        await using var held = new Npgsql.NpgsqlConnection(db.Connection);
        await held.OpenAsync(); await using var tx = await held.BeginTransactionAsync();
        await new Npgsql.NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:verification-account:{id}',0))", held, tx).ExecuteNonQueryAsync();
        Assert.Equal(0, await db.Scalar("SELECT public.cleanup_pending_registrations(100)"));
        await new Npgsql.NpgsqlCommand($"UPDATE users SET email_verified_at=now() WHERE id='{id}'", held, tx).ExecuteNonQueryAsync();
        await tx.CommitAsync();
        Assert.Equal(0, await db.Scalar("SELECT public.cleanup_pending_registrations(100)"));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM users WHERE id='{id}'"));
    }

    [BillingPostgresFact]
    public async Task Competing_batches_delete_each_account_once_and_failure_rolls_back_children()
    {
        await using var db = await BillingDatabase.Create(migrationHistory: true);
        var id = Guid.NewGuid();
        await db.Sql($$"""
            INSERT INTO users(id,email,username,role,registration_expires_at) VALUES('{{id}}','rollback@example.test','rollback_cleanup','Trainee',now()-interval '1 minute');
            INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at)
            VALUES(gen_random_uuid(),'{{id}}','{{id}}','cleanup-test',now(),now()+interval '1 day');
            CREATE FUNCTION fail_pending_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'delete unavailable'; END $$;
            CREATE TRIGGER fail_pending_delete BEFORE DELETE ON users FOR EACH ROW EXECUTE FUNCTION fail_pending_delete();
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Scalar("SELECT public.cleanup_pending_registrations(100)"));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{id}'"));
        await db.Sql("DROP TRIGGER fail_pending_delete ON users");
        var results = await Task.WhenAll(db.Scalar("SELECT public.cleanup_pending_registrations(100)"), db.Scalar("SELECT public.cleanup_pending_registrations(100)"));
        Assert.Equal(1, results.Sum(x => (int)x!));
    }
}
