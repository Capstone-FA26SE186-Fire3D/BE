using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.NameTranslation;
using Xunit;
using Fire3D.Infrastructure.Workers;
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
        private static readonly NpgsqlNullNameTranslator Names = new();
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
                  full_name text, username varchar(30), dob date, gender text, phone_number varchar(32), avatar_url varchar(2048), avatar_storage_key text, profile_revision bigint NOT NULL DEFAULT 1, password_hash text, role user_role_enum NOT NULL, is_active boolean NOT NULL DEFAULT true,
                  last_login_at timestamptz, created_at timestamptz NOT NULL DEFAULT now(),
                  updated_at timestamptz NOT NULL DEFAULT now(), deleted_at timestamptz,
                  registration_expires_at timestamptz, email_verified_at timestamptz);
                CREATE TABLE organizations (
                  id uuid PRIMARY KEY, registration_owner_user_id uuid NULL);
                CREATE TABLE buildings (id uuid PRIMARY KEY, organization_id uuid NOT NULL);
                CREATE TABLE sessions (id uuid PRIMARY KEY, trainee_user_id uuid NULL);
                CREATE TABLE user_devices (id uuid PRIMARY KEY, user_id uuid NULL);
                CREATE TABLE auth_refresh_tokens (
                  id uuid PRIMARY KEY,user_id uuid REFERENCES users(id),family_id uuid NOT NULL,
                  token_hash varchar(64) NOT NULL UNIQUE,created_at timestamptz NOT NULL,expires_at timestamptz NOT NULL,
                  consumed_at timestamptz,revoked_at timestamptz);
                CREATE TABLE password_reset_tokens (
                  id uuid PRIMARY KEY,user_id uuid NOT NULL REFERENCES users(id),
                  expires_at timestamptz NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),used_at timestamptz);
                CREATE TABLE audit_logs (
                  id uuid PRIMARY KEY,user_id uuid,organization_id uuid,actor_type text,action audit_action_enum,
                  target_entity text,target_id uuid,correlation_id uuid,old_values jsonb,new_values jsonb,
                  ip_address inet,user_agent text,created_at timestamptz NOT NULL DEFAULT now());
                """);
            await db.Sql(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"002_password_reset_recovery.sql")));
            await db.Sql(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"003_local_password.sql")));
            await db.Sql(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"005_email_verification.sql")));
            await db.Sql("ALTER TABLE email_verification_jobs ADD COLUMN user_id uuid, ADD COLUMN generation integer NOT NULL DEFAULT 1;");
            foreach (var operation in new Fire3D.Infrastructure.Migrations.AddRefreshCleanupGate().UpOperations
                .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()) await db.Sql(operation.Sql);
            foreach (var operation in new Fire3D.Infrastructure.Migrations.AddPasswordRecoveryGate().UpOperations
                .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()) await db.Sql(operation.Sql);
            return db;
        }
        private DbContextOptions<Fire3DDbContext>? contextOptions;
        public Fire3DDbContext Context() => new(contextOptions ??= new DbContextOptionsBuilder<Fire3DDbContext>()
            .UseNpgsql(Connection, p => {
                p.MapEnum<UserRole>("user_role_enum",nameTranslator:Names);
                p.MapEnum<AuditAction>("audit_action_enum",nameTranslator:Names);
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
    public async Task Local_register_login_reset_is_atomic_single_use_and_revokes_sessions()
    {
        await using var database=await Database.Create();
        await using var db=database.Context();
        var accounts=new AuthStore(db);var passwords=new PasswordService();
        // Minimal test enum only needs the extra label for registration audit.
        await database.Sql("ALTER TYPE audit_action_enum ADD VALUE IF NOT EXISTS 'Create'");
        var register=new Fire3D.Application.Authentication.Commands.RegisterUser.RegisterUserCommandHandler(accounts,passwords,
            ResetProxy.For<Fire3D.Application.Authentication.IRegistrationOtpService>((method,_) => method == "ConsumeRegistrationTokenAsync"
                ? Task.FromResult(Fire3D.Application.Authentication.AuthResult<bool>.Ok(true)) : throw new InvalidOperationException()),TimeProvider.System);
        var created=await register.Handle(new(" USER@EXAMPLE.TEST ","OriginalPassword12!","Test User","test-user","OriginalPassword12!", RegistrationToken: "proof"),default);
        Assert.True(created.IsSuccess,created.Error?.Message);
        var user=await accounts.FindUserByEmailAsync("user@example.test",default);
        Assert.NotNull(user);Assert.Null(user.FirebaseUid);Assert.NotEqual("OriginalPassword12!",user.PasswordHash);
        Assert.True(passwords.Verify(user,"OriginalPassword12!",out _));
        var tokens=new TokenService(Microsoft.Extensions.Options.Options.Create(new JwtOptions {
            Issuer="test",Audience="test",SigningKey=Convert.ToBase64String(new byte[64]) }));
        var login=new Fire3D.Application.Authentication.Commands.LoginWithPassword.LoginWithPasswordCommandHandler(accounts,passwords,tokens,TimeProvider.System);
        Assert.True((await login.Handle(new("USER@example.test","OriginalPassword12!"),default)).IsSuccess);
        Assert.True((await login.Handle(new("user@example.test","OriginalPassword12!"),default)).IsSuccess);
        Assert.Equal(401,(await login.Handle(new("user@example.test","wrong"),default)).Error?.Status);
        var reset=new LocalPasswordReset(db,accounts,passwords,Microsoft.Extensions.Options.Options.Create(new AuthEmailOptions { FrontendUrl="https://app.example.test" }));
        var link=await reset.CreateLinkAsync(user.Email,default);
        var raw=System.Web.HttpUtility.ParseQueryString(new Uri(link!).Query)["token"]!;
        Assert.Equal(64,raw.Length);
        Assert.NotEqual(raw,await database.Sql("SELECT token_hash FROM local_password_reset_tokens LIMIT 1"));
        Assert.True(await reset.ResetAsync(raw,"ReplacementPassword12!",default));
        Assert.False(await reset.ResetAsync(raw,"AnotherPassword12!",default));
        Assert.Equal(0L,await database.Sql("SELECT count(*) FROM auth_refresh_tokens WHERE revoked_at IS NULL"));
        Assert.Equal(401,(await login.Handle(new(user.Email,"OriginalPassword12!"),default)).Error?.Status);
        Assert.True((await login.Handle(new(user.Email,"ReplacementPassword12!"),default)).IsSuccess);

        link=await reset.CreateLinkAsync(user.Email,default);
        raw=System.Web.HttpUtility.ParseQueryString(new Uri(link!).Query)["token"]!;
        await database.Sql("ALTER TABLE audit_logs ADD CONSTRAINT fail_new_audit CHECK (actor_type='Impossible') NOT VALID");
        await Assert.ThrowsAsync<DbUpdateException>(()=>reset.ResetAsync(raw,"MustNotPersist12!",default));
        await using var fresh=database.Context();
        var current=await new AuthStore(fresh).FindUserAsync(user.Id,default);
        Assert.True(passwords.Verify(current!,"ReplacementPassword12!",out _));
        Assert.Equal(1L,await database.Sql("SELECT count(*) FROM local_password_reset_tokens WHERE used_at IS NULL"));
        Assert.Equal(1L,await database.Sql("SELECT count(*) FROM auth_refresh_tokens WHERE revoked_at IS NULL"));
    }

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
    public async Task Email_verification_deduplicates_fences_stale_worker_and_consumes_token_once()
    {
        await using var database = await Database.Create();
        var userId = Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role,registration_expires_at) VALUES ('{userId}','verify@example.com','Trainee',now()+interval '2 hours')");
        var settings = Options.Create(new AuthEmailOptions { FrontendUrl = "https://app.example.test" });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = database.Context();
            await new EmailVerificationQueue(context, settings).EnqueueAsync(userId, "verify@example.com", default);
        }));
        Assert.Equal(1L, await database.Sql("SELECT count(*) FROM email_verification_jobs"));

        await using var firstContext = database.Context();
        var firstQueue = new EmailVerificationQueue(firstContext, settings);
        var first = Assert.IsType<VerificationEmailJob>(await firstQueue.ClaimAsync(default));
        await database.Sql("UPDATE email_verification_jobs SET lease_until=now()-interval '1 second'");

        await using var secondContext = database.Context();
        var secondQueue = new EmailVerificationQueue(secondContext, settings);
        var second = Assert.IsType<VerificationEmailJob>(await secondQueue.ClaimAsync(default));
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Null(await firstQueue.CreateLinkAsync(first, default));

        var link = await secondQueue.CreateLinkAsync(second, default);
        var raw = new Uri(link!).Fragment["#token=".Length..];
        Assert.Equal(64, raw.Length);
        Assert.NotEqual(raw, await database.Sql("SELECT token_hash FROM email_verification_tokens LIMIT 1"));

        await using var verifyContext = database.Context();
        var verifyQueue = new EmailVerificationQueue(verifyContext, settings);
        Assert.True(await verifyQueue.VerifyAsync(raw.ToUpperInvariant(), default));
        Assert.False(await verifyQueue.VerifyAsync(raw, default));
        Assert.Equal(1L, await database.Sql("SELECT count(*) FROM users WHERE id='" + userId + "' AND email_verified_at IS NOT NULL"));
    }

    [ResetPostgresFact]
    public async Task Concurrent_verification_of_two_active_tokens_has_one_winner_without_deadlock()
    {
        await using var database = await Database.Create();
        var userId = Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role,registration_expires_at) VALUES ('{userId}','parallel@example.com','Trainee',now()+interval '2 hours')");
        var settings = Options.Create(new AuthEmailOptions { FrontendUrl = "https://app.example.test" });
        await using var setup = database.Context();
        var queue = new EmailVerificationQueue(setup, settings);
        await queue.EnqueueAsync(userId, "parallel@example.com", default);
        var job = Assert.IsType<VerificationEmailJob>(await queue.ClaimAsync(default));
        var first = await queue.CreateLinkAsync(job, default);
        var second = await queue.CreateLinkAsync(job, default);
        var firstToken = new Uri(first!).Fragment["#token=".Length..];
        var secondToken = new Uri(second!).Fragment["#token=".Length..];

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
        {
            await using var context = database.Context();
            return await new EmailVerificationQueue(context, settings).VerifyAsync(index == 0 ? firstToken : secondToken, default);
        }));

        Assert.Equal(1, outcomes.Count(result => result));
        Assert.Equal(1L, await database.Sql($"SELECT count(*) FROM users WHERE id='{userId}' AND email_verified_at IS NOT NULL"));
    }

    [ResetPostgresFact]
    public async Task Avatar_finalization_consumes_an_intent_once_under_concurrent_requests()
    {
        await using var database = await Database.Create();
        await database.Sql("""
            CREATE TABLE avatar_upload_intents (
              id uuid PRIMARY KEY, user_id uuid NOT NULL, staging_object_key text NOT NULL,
              content_type text NOT NULL, expected_size_bytes bigint NOT NULL,
              expires_at timestamptz NOT NULL, completed_at timestamptz NULL, final_object_key text NULL,
              expected_profile_revision bigint NULL, candidate_attempt_id uuid NULL, candidate_object_key text NULL,
              candidate_source_etag text NULL, candidate_lease_until timestamptz NULL,
              created_at timestamptz NOT NULL
            )
            """);
        var userId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role) VALUES ('{userId}','avatar-race@example.com','Trainee')");
        await database.Sql($"INSERT INTO avatar_upload_intents(id,user_id,staging_object_key,content_type,expected_size_bytes,expires_at,created_at) VALUES ('{intentId}','{userId}','avatars/staging/test','image/png',1024,now()+interval '5 minutes',now())");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async attempt =>
        {
            await using var context = database.Context();
            var store = new AvatarStore(context);
            var reservation = await store.ReserveCopyCandidateAsync(intentId, userId, 1, "etag", DateTime.UtcNow, default);
            return await store.FinalizeUploadAsync(intentId, userId, reservation.Candidate!.AttemptId, 1,
                reservation.Candidate.ObjectKey, DateTime.UtcNow, default);
        }));

        Assert.Equal(1, outcomes.Count(result => result.Status == Fire3D.Application.Authentication.Avatar.AvatarFinalizeStatus.Finalized));
        Assert.Equal(1L, await database.Sql($"SELECT count(*) FROM avatar_upload_intents WHERE id='{intentId}' AND completed_at IS NOT NULL"));
        Assert.Equal(2L, await database.Sql($"SELECT profile_revision FROM users WHERE id='{userId}'"));
    }

    [ResetPostgresFact]
    public async Task Resend_keeps_an_already_issued_link_valid_until_it_expires()
    {
        await using var database = await Database.Create();
        var userId = Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role,registration_expires_at) VALUES ('{userId}','resend@example.com','Trainee',now()+interval '2 hours')");
        var settings = Options.Create(new AuthEmailOptions { FrontendUrl = "https://app.example.test" });

        await using var firstContext = database.Context();
        var firstQueue = new EmailVerificationQueue(firstContext, settings);
        await firstQueue.EnqueueAsync(userId, "resend@example.com", default);
        var firstJob = Assert.IsType<VerificationEmailJob>(await firstQueue.ClaimAsync(default));
        var firstLink = Assert.IsType<string>(await firstQueue.CreateLinkAsync(firstJob, default));
        var firstToken = new Uri(firstLink).Fragment["#token=".Length..];
        await firstQueue.CompleteAsync(firstJob, default);

        await using var resendContext = database.Context();
        var resendQueue = new EmailVerificationQueue(resendContext, settings);
        await resendQueue.EnqueueAsync("resend@example.com", default);

        await using var verifyContext = database.Context();
        Assert.True(await new EmailVerificationQueue(verifyContext, settings).VerifyAsync(firstToken, default));
    }

    [ResetPostgresFact]
    public async Task Resend_enforces_one_minute_account_wide_cooldown_then_creates_next_generation()
    {
        await using var database = await Database.Create();
        var userId = Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role,registration_expires_at) VALUES ('{userId}','cooldown@example.com','Trainee',now()+interval '2 hours')");
        var settings = Options.Create(new AuthEmailOptions { FrontendUrl = "https://app.example.test" });

        await using (var initialContext = database.Context())
            await new EmailVerificationQueue(initialContext, settings).EnqueueAsync(userId, "cooldown@example.com", default);
        await using (var firstResendContext = database.Context())
            await new EmailVerificationQueue(firstResendContext, settings).EnqueueAsync("cooldown@example.com", default);
        Assert.Equal(1L, await database.Sql($"SELECT count(*) FROM email_verification_jobs WHERE user_id='{userId}'"));

        await database.Sql($"UPDATE email_verification_jobs SET created_at=now()-interval '61 seconds' WHERE user_id='{userId}'");
        await using (var secondResendContext = database.Context())
            await new EmailVerificationQueue(secondResendContext, settings).EnqueueAsync("cooldown@example.com", default);
        Assert.Equal(2L, await database.Sql($"SELECT count(*) FROM email_verification_jobs WHERE user_id='{userId}'"));
        Assert.Equal(2, Assert.IsType<int>(await database.Sql($"SELECT max(generation) FROM email_verification_jobs WHERE user_id='{userId}'")));
    }

    [ResetPostgresFact]
    public async Task Concurrent_legacy_resends_create_one_generation_and_fence_the_old_job()
    {
        await using var database=await Database.Create();var user=Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role,registration_expires_at) VALUES('{user}','resend-race@example.test','Trainee',now()+interval '2 hours')");
        var options=Options.Create(new AuthEmailOptions{FrontendUrl="https://app.example.test"});
        await using var initial=database.Context();var queue=new EmailVerificationQueue(initial,options);
        await queue.EnqueueAsync(user,"resend-race@example.test",default);
        var oldJob=Assert.IsType<VerificationEmailJob>(await queue.ClaimAsync(default));
        await database.Sql("UPDATE email_verification_jobs SET created_at=now()-interval '61 seconds'");
        await Task.WhenAll(Enumerable.Range(0,5).Select(async _=>
        {
            await using var context=database.Context();
            await new EmailVerificationQueue(context,options).EnqueueAsync("resend-race@example.test",default);
        }));
        Assert.Equal(2L,await database.Sql("SELECT count(*) FROM email_verification_jobs"));
        Assert.Equal(2,Assert.IsType<int>(await database.Sql("SELECT max(generation) FROM email_verification_jobs")));
        Assert.False(await queue.CanDeliverAsync(oldJob,default));
        Assert.Null(await queue.CreateLinkAsync(oldJob,default));
    }

    [ResetPostgresFact]
    public async Task Cleanup_removes_only_the_organization_owned_by_the_expired_registration()
    {
        await using var database = await Database.Create();
        var ownedOrganization = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var retainedOrganization = Guid.NewGuid();
        var retainedUser = Guid.NewGuid();
        await database.Sql($"INSERT INTO organizations(id,registration_owner_user_id) VALUES ('{ownedOrganization}','{owner}'),('{retainedOrganization}','{Guid.NewGuid()}')");
        await database.Sql($"INSERT INTO users(id,organization_id,email,role,registration_expires_at) VALUES ('{owner}','{ownedOrganization}','expired-owner@example.com','Trainee',now()-interval '1 second'),('{retainedUser}','{retainedOrganization}','expired-retained@example.com','Trainee',now()-interval '1 second')");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<Fire3DDbContext>(_ => database.Context());
        await using var provider = services.BuildServiceProvider();
        var worker = new PendingRegistrationCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<PendingRegistrationCleanupWorker>>());

        Assert.Equal(2, await worker.DeleteBatchAsync(default));
        Assert.Equal(0L, await database.Sql($"SELECT count(*) FROM users WHERE id IN ('{owner}','{retainedUser}')"));
        Assert.Equal(0L, await database.Sql($"SELECT count(*) FROM organizations WHERE id='{ownedOrganization}'"));
        Assert.Equal(1L, await database.Sql($"SELECT count(*) FROM organizations WHERE id='{retainedOrganization}'"));
    }

    [ResetPostgresFact]
    public async Task Cleanup_skips_an_expired_registration_while_its_verification_account_lock_is_held()
    {
        await using var database = await Database.Create();
        var userId = Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role,registration_expires_at) VALUES ('{userId}','locked@example.com','Trainee',now()-interval '1 second')");
        await using var held = new NpgsqlConnection(database.Connection);
        await held.OpenAsync();
        await using var transaction = await held.BeginTransactionAsync();
        await new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:verification-account:{userId}', 0))", held, transaction).ExecuteNonQueryAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<Fire3DDbContext>(_ => database.Context());
        await using var provider = services.BuildServiceProvider();
        var worker = new PendingRegistrationCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<PendingRegistrationCleanupWorker>>());

        Assert.Equal(0, await worker.DeleteBatchAsync(default));
        Assert.Equal(1L, await database.Sql($"SELECT count(*) FROM users WHERE id='{userId}'"));
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

    [ResetPostgresFact]
    public async Task Refresh_token_cleanup_removes_only_families_past_the_retention_window()
    {
        await using var database = await Database.Create();
        var userId = Guid.NewGuid();
        var removableFamily = Guid.NewGuid();
        var recentFamily = Guid.NewGuid();
        var mixedFamily = Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role) VALUES ('{userId}','cleanup@example.test','Trainee')");
        await database.Sql($"""
            INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at,consumed_at)
            VALUES
              ('{Guid.NewGuid()}','{userId}','{removableFamily}','{Guid.NewGuid():N}',now()-interval '20 days',now()-interval '8 days',now()-interval '19 days'),
              ('{Guid.NewGuid()}','{userId}','{removableFamily}','{Guid.NewGuid():N}',now()-interval '19 days',now()-interval '8 days',NULL),
              ('{Guid.NewGuid()}','{userId}','{recentFamily}','{Guid.NewGuid():N}',now()-interval '8 days',now()-interval '6 days',NULL),
              ('{Guid.NewGuid()}','{userId}','{mixedFamily}','{Guid.NewGuid():N}',now()-interval '20 days',now()-interval '8 days',now()-interval '19 days'),
              ('{Guid.NewGuid()}','{userId}','{mixedFamily}','{Guid.NewGuid():N}',now()-interval '8 days',now()-interval '6 days',NULL)
            """);

        await using var context = database.Context();
        var cleanup = new RefreshTokenCleanupStore(context);

        Assert.Equal(2, await cleanup.DeleteExpiredFamiliesAsync(retentionDays: 7, batchSize: 20, ct: default));
        Assert.Equal(0L, await database.Sql($"SELECT count(*) FROM auth_refresh_tokens WHERE family_id='{removableFamily}'"));
        Assert.Equal(1L, await database.Sql($"SELECT count(*) FROM auth_refresh_tokens WHERE family_id='{recentFamily}'"));
        Assert.Equal(2L, await database.Sql($"SELECT count(*) FROM auth_refresh_tokens WHERE family_id='{mixedFamily}'"));
    }

    [ResetPostgresFact]
    public async Task Refresh_cleanup_batches_have_stable_user_and_family_order()
    {
        await using var database=await Database.Create();
        var firstUser=Guid.Parse("00000000-0000-0000-0000-000000000010");
        var secondUser=Guid.Parse("00000000-0000-0000-0000-000000000020");
        var firstFamily=Guid.Parse("00000000-0000-0000-0000-000000000005");
        var nextFamily=Guid.Parse("00000000-0000-0000-0000-000000000008");
        await database.Sql($"INSERT INTO users(id,email,role) VALUES('{firstUser}','order-one@example.test','Trainee'),('{secondUser}','order-two@example.test','Trainee')");
        await database.Sql($"""
            INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at)
            VALUES ('{Guid.NewGuid()}','{secondUser}','{firstFamily}','second',now()-interval '20 days',now()-interval '8 days'),
                   ('{Guid.NewGuid()}','{firstUser}','{nextFamily}','next',now()-interval '20 days',now()-interval '8 days'),
                   ('{Guid.NewGuid()}','{firstUser}','{firstFamily}','first',now()-interval '20 days',now()-interval '8 days')
            """);
        await using var context=database.Context();var cleanup=new RefreshTokenCleanupStore(context);
        Assert.Equal(1,await cleanup.DeleteExpiredFamiliesAsync(7,1,default));
        Assert.Equal(0L,await database.Sql($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{firstUser}' AND family_id='{firstFamily}'"));
        Assert.Equal(2L,await database.Sql("SELECT count(*) FROM auth_refresh_tokens"));
        Assert.Equal(1,await cleanup.DeleteExpiredFamiliesAsync(7,1,default));
        Assert.Equal(secondUser,await database.Sql("SELECT user_id FROM auth_refresh_tokens"));
    }

    [ResetPostgresFact]
    public async Task Refresh_cleanup_rechecks_the_family_after_waiting_for_the_user_lock()
    {
        await using var database=await Database.Create();var user=Guid.NewGuid();var family=Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role) VALUES('{user}','cleanup-race@example.test','Trainee'); INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES('{Guid.NewGuid()}','{user}','{family}','old',now()-interval '20 days',now()-interval '8 days')");
        await using var writer=new NpgsqlConnection(database.Connection);await writer.OpenAsync();await using var tx=await writer.BeginTransactionAsync();
        await new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:auth:{user}',0))",writer,tx).ExecuteNonQueryAsync();
        await using var context=database.Context();var cleanup=new RefreshTokenCleanupStore(context);
        var pending=cleanup.DeleteExpiredFamiliesAsync(7,20,default);
        // Observe the real cleanup connection waiting on the lock, rather than racing an arbitrary sleep.
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!Equals(await database.Sql("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')"),true))
        {
            if(pending.IsCompleted)await pending;
            await Task.Delay(20,timeout.Token);
        }
        await new NpgsqlCommand($"INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES('{Guid.NewGuid()}','{user}','{family}','active',now(),now()+interval '1 day')",writer,tx).ExecuteNonQueryAsync();
        await tx.CommitAsync();
        Assert.Equal(0,await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2L,await database.Sql("SELECT count(*) FROM auth_refresh_tokens"));
    }

    [ResetPostgresFact]
    public async Task Refresh_cleanup_keeps_recently_expired_and_active_families_at_retention_boundary()
    {
        await using var database=await Database.Create();var user=Guid.NewGuid();
        await database.Sql($"INSERT INTO users(id,email,role) VALUES('{user}','cleanup-boundary@example.test','Trainee'); INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES('{Guid.NewGuid()}','{user}','{Guid.NewGuid()}','boundary',now()-interval '20 days',now()-interval '7 days'),('{Guid.NewGuid()}','{user}','{Guid.NewGuid()}','retained',now()-interval '20 days',now()-interval '7 days'+interval '5 minutes'),('{Guid.NewGuid()}','{user}','{Guid.NewGuid()}','active',now(),now()+interval '1 day')");
        await using var context=database.Context();
        Assert.Equal(1,await new RefreshTokenCleanupStore(context).DeleteExpiredFamiliesAsync(7,20,default));
        Assert.Equal(2L,await database.Sql("SELECT count(*) FROM auth_refresh_tokens WHERE token_hash IN('retained','active')"));
    }
}
