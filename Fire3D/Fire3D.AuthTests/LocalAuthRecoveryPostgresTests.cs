using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.LoginWithPassword;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using Npgsql;

namespace Fire3D.AuthTests;

public sealed class LocalAuthRecoveryPostgresTests
{
    private static readonly PasswordService Passwords = new();
    private const string OldPassword = "old-password";
    private const string NewPassword = "new-password";

    private static LocalPasswordReset Recovery(Fire3D.Infrastructure.Persistence.Fire3DDbContext context) =>
        new(context, new AuthStore(context), Passwords, Options.Create(new AuthEmailOptions { FrontendUrl = "https://example.test" }));

    private static async Task Prepare(BillingDatabase database)
    {
        await using var context = database.Context();
        var user = await new AuthStore(context).FindUserAsync(BillingDatabase.Owner, default);
        await new AuthStore(context).UpdatePasswordHashAsync(user!.Id, Passwords.Hash(user, OldPassword), DateTime.UtcNow, default);
        await database.Sql("""
            CREATE TABLE local_password_reset_tokens (
              id uuid PRIMARY KEY,user_id uuid NOT NULL REFERENCES users(id),token_hash text UNIQUE NOT NULL,
              created_at timestamptz DEFAULT now(),expires_at timestamptz NOT NULL,used_at timestamptz);
            INSERT INTO password_reset_tokens(id,user_id,expires_at)
              VALUES (gen_random_uuid(),'10000000-0000-0000-0000-000000000002',now()+interval '1 hour');
            CREATE ROLE auth_recovery_runtime NOLOGIN NOSUPERUSER NOBYPASSRLS;
            GRANT USAGE ON SCHEMA public TO auth_recovery_runtime;
            GRANT SELECT,UPDATE ON users,auth_refresh_tokens TO auth_recovery_runtime;
            GRANT SELECT ON organizations TO auth_recovery_runtime;
            GRANT SELECT,INSERT,UPDATE ON local_password_reset_tokens TO auth_recovery_runtime;
            GRANT INSERT ON audit_logs TO auth_recovery_runtime;
            ALTER TABLE password_reset_tokens ENABLE ROW LEVEL SECURITY;
            REVOKE ALL ON password_reset_tokens FROM PUBLIC,auth_recovery_runtime;
            """);
        await BackendDatabasePermissionsTests.Apply(database, "AddPasswordRecoveryGate");
        if (Equals(await database.Scalar("SELECT to_regprocedure('public.invalidate_legacy_reset_tokens(uuid)') IS NOT NULL"), true))
            await database.Sql("GRANT EXECUTE ON FUNCTION public.invalidate_legacy_reset_tokens(uuid) TO auth_recovery_runtime");
    }

    // Each test uses a separate database, but a role is cluster-wide.
    // Serialize this fixture and remove its own grants/role at the end.
    private static async Task DropRuntime(BillingDatabase database) =>
        await database.Sql("DROP OWNED BY auth_recovery_runtime; DROP ROLE auth_recovery_runtime");

    [BillingPostgresFact]
    public async Task Restricted_runtime_changes_password_without_deleting_legacy_tokens()
    {
        await using var database = await BillingDatabase.Create(false);
        await Prepare(database);
        try
        {
            await using var context = database.Context();
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync("SET ROLE auth_recovery_runtime");
            Assert.Equal(false, await database.Scalar("SELECT has_table_privilege('auth_recovery_runtime','password_reset_tokens','DELETE')"));
            var result = await Recovery(context).ChangeAsync(BillingDatabase.Owner, BillingDatabase.Owner, OldPassword, NewPassword, default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(1L, await database.Scalar("SELECT count(*) FROM password_reset_tokens WHERE used_at IS NOT NULL"));
            Assert.Equal(0L, await database.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Owner}' AND revoked_at IS NULL"));
            Assert.Equal(1L, await database.Scalar("SELECT count(*) FROM audit_logs WHERE action='Update'"));
        }
        finally { await DropRuntime(database); }
    }

    [BillingPostgresFact]
    public async Task Legacy_mixed_case_email_is_found_and_can_login()
    {
        await using var database = await BillingDatabase.Create(false);
        await Prepare(database);
        try
        {
            await database.Sql($"UPDATE users SET email=' OWNER@EXAMPLE.TEST ' WHERE id='{BillingDatabase.Owner}'; CREATE TABLE password_reset_operations(user_id uuid,status text,finished_at timestamptz)");
            await using var context = database.Context();
            var store = new AuthStore(context);
            Assert.NotNull(await store.FindUserByEmailAsync("owner@example.test", default));
            var tokens = new TokenService(Options.Create(new JwtOptions { Issuer = "test", Audience = "test", SigningKey = new string('x', 64) }));
            var result = await new LoginWithPasswordCommandHandler(store, Passwords, tokens, TimeProvider.System)
                .Handle(new("owner@example.test", OldPassword), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }
        finally { await DropRuntime(database); }
    }

    private static async Task AssumeRuntime(Fire3D.Infrastructure.Persistence.Fire3DDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET ROLE auth_recovery_runtime");
    }

    [BillingPostgresFact]
    public async Task Restricted_reset_consumes_all_tokens_once_and_preserves_other_users()
    {
        await using var database = await BillingDatabase.Create(false);
        await Prepare(database);
        try
        {
            await database.Sql($"INSERT INTO password_reset_tokens(id,user_id,expires_at) VALUES(gen_random_uuid(),'{BillingDatabase.Other}',now()+interval '1 hour')");
            await using var context = database.Context();
            await AssumeRuntime(context);
            var recovery = Recovery(context);
            var first = await recovery.CreateLinkAsync("owner@example.test", default);
            var second = await recovery.CreateLinkAsync("owner@example.test", default);
            var token = new Uri(first!).Query.Split('=')[1];
            var secondToken = new Uri(second!).Query.Split('=')[1];
            Assert.True(await recovery.ResetAsync(token, NewPassword, default));
            Assert.False(await recovery.ResetAsync(token, OldPassword, default));
            Assert.False(await recovery.ResetAsync(secondToken, OldPassword, default));
            Assert.Equal(2L, await database.Scalar("SELECT count(*) FROM local_password_reset_tokens WHERE used_at IS NOT NULL"));
            Assert.Equal(1L, await database.Scalar($"SELECT count(*) FROM password_reset_tokens WHERE user_id='{BillingDatabase.Owner}' AND used_at IS NOT NULL"));
            Assert.Equal(1L, await database.Scalar($"SELECT count(*) FROM password_reset_tokens WHERE user_id='{BillingDatabase.Other}' AND used_at IS NULL"));
            Assert.Equal(1L, await database.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Other}' AND revoked_at IS NULL"));
            Assert.Equal(false, await database.Scalar("SELECT has_table_privilege('auth_recovery_runtime','password_reset_tokens','UPDATE')"));
            Assert.Equal(false, await database.Scalar("SELECT has_schema_privilege('fet3d_password_recovery_owner','public','CREATE')"));
        }
        finally { await DropRuntime(database); }
    }

    [BillingPostgresFact]
    public async Task Restricted_reset_rolls_back_password_tokens_and_sessions_when_audit_fails()
    {
        await using var database = await BillingDatabase.Create(false);
        await Prepare(database);
        try
        {
            await using var context = database.Context();
            await AssumeRuntime(context);
            var recovery = Recovery(context);
            var link = await recovery.CreateLinkAsync("owner@example.test", default);
            var token = new Uri(link!).Query.Split('=')[1];
            await database.Sql("ALTER TABLE audit_logs ADD CONSTRAINT reject_audit CHECK (false) NOT VALID");
            await Assert.ThrowsAsync<DbUpdateException>(() => recovery.ResetAsync(token, NewPassword, default));
            await using var fresh = database.Context();
            Assert.True(Passwords.Verify((await new AuthStore(fresh).FindUserAsync(BillingDatabase.Owner, default))!, OldPassword, out _));
            Assert.Equal(1L, await database.Scalar("SELECT count(*) FROM local_password_reset_tokens WHERE used_at IS NULL"));
            Assert.Equal(1L, await database.Scalar("SELECT count(*) FROM password_reset_tokens WHERE used_at IS NULL"));
            Assert.Equal(1L, await database.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Owner}' AND revoked_at IS NULL"));
        }
        finally { await DropRuntime(database); }
    }

    [BillingPostgresFact]
    public async Task Concurrent_reset_requests_have_exactly_one_winner()
    {
        await using var database = await BillingDatabase.Create(false);
        await Prepare(database);
        try
        {
            await using var context = database.Context();
            var link = await Recovery(context).CreateLinkAsync("owner@example.test", default);
            var token = new Uri(link!).Query.Split('=')[1];
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var request = database.Context();
                await AssumeRuntime(request);
                return await Recovery(request).ResetAsync(token, NewPassword, default);
            }));
            Assert.Single(results, result => result);
            Assert.Equal(1L, await database.Scalar("SELECT count(*) FROM audit_logs WHERE action='Update'"));
        }
        finally { await DropRuntime(database); }
    }

    [BillingPostgresFact]
    public async Task Change_waiting_for_user_lock_rejects_revoked_family_and_preserves_new_login()
    {
        await using var database = await BillingDatabase.Create(false);
        await Prepare(database);
        try
        {
            await using var writer = new NpgsqlConnection(database.Connection);
            await writer.OpenAsync();
            await using var transaction = await writer.BeginTransactionAsync();
            await new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:auth:{BillingDatabase.Owner}',0))", writer, transaction).ExecuteNonQueryAsync();
            await using var request = database.Context();
            await AssumeRuntime(request);
            var change = Recovery(request).ChangeAsync(BillingDatabase.Owner, BillingDatabase.Owner, OldPassword, NewPassword, default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!Equals(await database.Scalar("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')"), true))
            {
                Assert.False(change.IsCompleted, "Change-password did not wait for the user lock.");
                await Task.Delay(20, timeout.Token);
            }
            var newFamily = Guid.NewGuid();
            await new NpgsqlCommand($"""
                UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{BillingDatabase.Owner}';
                INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at)
                VALUES(gen_random_uuid(),'{BillingDatabase.Owner}','{newFamily}','new-login',now(),now()+interval '1 day');
                """, writer, transaction).ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            Assert.Equal(401, (await change.WaitAsync(TimeSpan.FromSeconds(15))).Error?.Status);
            Assert.Equal(1L, await database.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE family_id='{newFamily}' AND revoked_at IS NULL"));
            Assert.Equal(0L, await database.Scalar("SELECT count(*) FROM audit_logs"));
        }
        finally { await DropRuntime(database); }
    }

    [BillingPostgresFact]
    public async Task Disabled_organization_cannot_generate_or_consume_a_reset_link()
    {
        await using var database = await BillingDatabase.Create(false);
        await Prepare(database);
        try
        {
            await using var context = database.Context();
            var recovery = Recovery(context);
            var link = await recovery.CreateLinkAsync("owner@example.test", default);
            await database.Sql($"UPDATE organizations SET is_active=false WHERE id='{BillingDatabase.Org}'");
            Assert.Null(await recovery.CreateLinkAsync("owner@example.test", default));
            Assert.False(await recovery.ResetAsync(new Uri(link!).Query.Split('=')[1], NewPassword, default));
            Assert.Equal(1L, await database.Scalar("SELECT count(*) FROM local_password_reset_tokens WHERE used_at IS NULL"));
        }
        finally { await DropRuntime(database); }
    }
}
