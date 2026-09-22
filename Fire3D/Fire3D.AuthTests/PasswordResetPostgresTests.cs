using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.NameTranslation;
using Xunit;
namespace Fire3D.AuthTests;

// Explicit opt-in: never fall back to appsettings or a developer/production database.
public sealed class ResetPostgresFactAttribute : FactAttribute
{
    public ResetPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIRE3D_RESET_TEST_ADMIN")))
            Skip = "Set FIRE3D_RESET_TEST_ADMIN to a disposable local PostgreSQL server.";
    }
}
public sealed class PasswordResetPostgresTests
{
    private sealed class Database : IAsyncDisposable
    {
        private readonly string admin;
        private readonly string name = "fire3d_reset_test_" + Guid.NewGuid().ToString("N");
        public string Connection = "";
        private Database(string admin) { this.admin = admin; }
        public static async Task<Database> Create()
        {
            var admin = Environment.GetEnvironmentVariable("FIRE3D_RESET_TEST_ADMIN")!;
            var settings = new NpgsqlConnectionStringBuilder(admin);
            if (settings.Host is not ("localhost" or "127.0.0.1" or "::1"))
                throw new InvalidOperationException("Integration tests require loopback PostgreSQL.");
            var db = new Database(admin);
            await using var c = new NpgsqlConnection(admin);
            await c.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {db.name}", c).ExecuteNonQueryAsync();
            settings.Database = db.name;
            db.Connection = settings.ConnectionString;
            await db.Sql("""
                CREATE TYPE user_role_enum AS ENUM ('PlatformAdmin','OrganizationAdmin','Trainer','Trainee');
                CREATE TYPE audit_action_enum AS ENUM ('Update','Login');
                CREATE TABLE users (
                  id uuid PRIMARY KEY, organization_id uuid, email text NOT NULL, firebase_uid text,
                  full_name text, role user_role_enum NOT NULL, is_active boolean NOT NULL DEFAULT true,
                  last_login_at timestamptz, created_at timestamptz NOT NULL DEFAULT now(),
                  updated_at timestamptz NOT NULL DEFAULT now(), deleted_at timestamptz);
                CREATE TABLE auth_refresh_tokens (
                  id uuid PRIMARY KEY,user_id uuid REFERENCES users(id),family_id uuid NOT NULL,
                  token_hash varchar(64) NOT NULL UNIQUE,created_at timestamptz NOT NULL,expires_at timestamptz NOT NULL,
                  consumed_at timestamptz,revoked_at timestamptz);
                CREATE TABLE audit_logs (
                  id uuid PRIMARY KEY,user_id uuid,organization_id uuid,actor_type text,action audit_action_enum,
                  target_entity text,target_id uuid,correlation_id uuid,old_values jsonb,new_values jsonb,
                  ip_address inet,user_agent text,created_at timestamptz NOT NULL DEFAULT now());
                """);
            await db.Sql(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"002_password_reset_recovery.sql")));
            return db;
        }
        public Fire3DDbContext Context() => new(new DbContextOptionsBuilder<Fire3DDbContext>()
            .UseNpgsql(Connection, p => {
                p.MapEnum<UserRole>("user_role_enum",nameTranslator:new NpgsqlNullNameTranslator());
                p.MapEnum<AuditAction>("audit_action_enum",nameTranslator:new NpgsqlNullNameTranslator());
            }).Options);
        public async Task<object?> Sql(string sql)
        {
            await using var c = new NpgsqlConnection(Connection);
            await c.OpenAsync();
            return await new NpgsqlCommand(sql,c).ExecuteScalarAsync();
        }
        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var c = new NpgsqlConnection(admin);
            await c.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)",c).ExecuteNonQueryAsync();
        }
    }
    private static RefreshToken Token(Guid user, DateTime? created = null) => new() {
        Id=Guid.NewGuid(),UserId=user,FamilyId=Guid.NewGuid(),TokenHash=Guid.NewGuid().ToString("N"),
        CreatedAt=created??DateTime.UtcNow,ExpiresAt=DateTime.UtcNow.AddDays(7) };

    [ResetPostgresFact]
    public async Task Queue_deduplicates_parallel_requests_and_only_one_worker_claims()
    {
        await using var db = await Database.Create();
        await Task.WhenAll(Enumerable.Range(0,8).Select(async _ => {
            await using var c=db.Context();
            await new PasswordResetQueue(c).EnqueueAsync("test@example.com",default);
        }));
        Assert.Equal(1L,await db.Sql("SELECT count(*) FROM password_reset_email_jobs"));
        var claims = await Task.WhenAll(Enumerable.Range(0,8).Select(async _ => {
            await using var c=db.Context();return await new PasswordResetQueue(c).ClaimAsync(default);
        }));
        Assert.Single(claims,x=>x!=null);
    }
    [ResetPostgresFact]
    public async Task Lease_recovery_fences_stale_ack_and_retries_with_backoff()
    {
        await using var db=await Database.Create();
        await using var c=db.Context();var queue=new PasswordResetQueue(c);
        await queue.EnqueueAsync("test@example.com",default);
        var first=(await queue.ClaimAsync(default))!;
        await db.Sql("UPDATE password_reset_email_jobs SET lease_until=now()-interval '1 second'");
        var second=(await queue.ClaimAsync(default))!;
        Assert.NotEqual(first.LeaseToken,second.LeaseToken);Assert.Equal(2,second.Attempt);
        await queue.CompleteAsync(first,default);
        Assert.Equal("Leased",await db.Sql("SELECT status FROM password_reset_email_jobs"));
        await queue.FailAsync(second,false,default);
        Assert.Null(await queue.ClaimAsync(default));
        await db.Sql("UPDATE password_reset_email_jobs SET available_at=now()-interval '1 second',attempts=4");
        var last=(await queue.ClaimAsync(default))!;Assert.Equal(5,last.Attempt);
        await db.Sql("UPDATE password_reset_email_jobs SET lease_until=now()-interval '1 second'");
        Assert.Null(await queue.ClaimAsync(default));
        Assert.Equal("Dead",await db.Sql("SELECT status FROM password_reset_email_jobs"));
    }
    [ResetPostgresFact]
    public async Task Reset_commit_revokes_all_sessions_and_blocks_stale_or_pending_login()
    {
        await using var db=await Database.Create();var user=Guid.NewGuid();
        await db.Sql($"INSERT INTO users(id,email,firebase_uid,role) VALUES ('{user}','test@example.com','uid','Trainee')");
        await using var c=db.Context();var auth=new AuthStore(c);var resets=new PasswordResetStore(c,auth);
        await auth.AddRefreshTokenAsync(Token(user),default);
        await auth.AddRefreshTokenAsync(Token(user),default);
        var stale=Token(user);
        var operation=await resets.BeginAsync(user,"uid",new string('a',64),default);
        Assert.NotNull(operation);
        Assert.Equal(2L,await db.Sql("SELECT count(*) FROM auth_refresh_tokens WHERE revoked_at IS NOT NULL"));
        Assert.Null(await resets.BeginAsync(user,"uid",new string('b',64),default));
        await Assert.ThrowsAsync<PasswordResetException>(()=>auth.AddRefreshTokenAsync(Token(user),default));
        await resets.FinishAsync(operation.Value,user,"Completed",default);
        await Assert.ThrowsAsync<PasswordResetException>(()=>auth.AddRefreshTokenAsync(stale,default));
        await auth.AddRefreshTokenAsync(Token(user),default);
        Assert.Equal(1L,await db.Sql("SELECT count(*) FROM auth_refresh_tokens WHERE revoked_at IS NULL"));
        Assert.Equal(1L,await db.Sql("SELECT count(*) FROM audit_logs"));
        Assert.Null(await resets.BeginAsync(user,"uid",new string('a',64),default));
    }
    [ResetPostgresFact]
    public async Task Finalization_database_failure_keeps_durable_fence_and_revocations()
    {
        await using var db=await Database.Create();var user=Guid.NewGuid();
        await db.Sql($"INSERT INTO users(id,email,firebase_uid,role) VALUES ('{user}','test@example.com','uid','Trainee')");
        await using(var c=db.Context()) {
            var auth=new AuthStore(c);await auth.AddRefreshTokenAsync(Token(user),default);
            var resets=new PasswordResetStore(c,auth);
            var op=await resets.BeginAsync(user,"uid",new string('c',64),default);
            await db.Sql("ALTER TABLE audit_logs ADD CONSTRAINT simulate_unavailable_audit CHECK (actor_type='Impossible')");
            await Assert.ThrowsAsync<DbUpdateException>(()=>resets.FinishAsync(op!.Value,user,"Completed",default));
        }
        Assert.Equal("Pending",await db.Sql("SELECT status FROM password_reset_operations"));
        Assert.Equal(1L,await db.Sql("SELECT count(*) FROM auth_refresh_tokens WHERE revoked_at IS NOT NULL"));
        await using var fresh=db.Context();
        await Assert.ThrowsAsync<PasswordResetException>(()=>new AuthStore(fresh).AddRefreshTokenAsync(Token(user),default));
    }
}
