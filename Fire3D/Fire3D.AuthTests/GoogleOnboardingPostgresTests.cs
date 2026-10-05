using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Domain.Enums;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleOnboardingPostgresTests
{
    internal static async Task Prepare(BillingDatabase db, bool includeDisplayName = true)
    {
        await db.Sql("""
            DO $$ BEGIN
             IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='anon') THEN CREATE ROLE anon NOLOGIN; END IF;
             IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='authenticated') THEN CREATE ROLE authenticated NOLOGIN; END IF;
             IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN CREATE ROLE fire3d_api NOLOGIN NOSUPERUSER NOBYPASSRLS; END IF;
            END $$;
            GRANT USAGE ON SCHEMA public TO fire3d_api;
            GRANT SELECT,INSERT,UPDATE ON users,organizations TO fire3d_api;
            GRANT INSERT ON audit_logs TO fire3d_api;
            """);
        await BackendDatabasePermissionsTests.Apply(db, "AddSelfRegistration");
        await BackendDatabasePermissionsTests.Apply(db, "AddNormalizedRegistrationEmail");
        await BackendDatabasePermissionsTests.Apply(db, "AddGoogleOnboarding");
        if (includeDisplayName) await BackendDatabasePermissionsTests.Apply(db, "AddGoogleOnboardingDisplayName");
    }

    private static GoogleOnboardingService Service(Fire3D.Infrastructure.Persistence.Fire3DDbContext db, TimeProvider? clock = null) =>
        new(db, new AuthStore(db), clock ?? TimeProvider.System);
    private static async Task<string> Begin(BillingDatabase db, string uid = "new-google", string email = "new@example.test", TimeProvider? clock = null)
    {
        await using var context = db.Context();
        var result = await Service(context, clock).BeginAsync(new(uid, email), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!.Token;
    }
    private static GoogleOnboardingCompleteRequest Trainee(string token, string username = "google_user") => new(token, UserRole.Trainee, username, "Google User");

    [BillingPostgresFact]
    public async Task Proof_quota_is_serialized_per_verified_uid()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        for (var i = 0; i < 9; i++) await Begin(db);
        async Task<AuthResult<GoogleOnboardingProof>> Request()
        {
            await using var context = db.Context(); return await Service(context).BeginAsync(new("new-google", "new@example.test"), default);
        }
        var results = await Task.WhenAll(Request(), Request());
        Assert.Single(results, x => x.IsSuccess);
        Assert.Equal("GOOGLE_ONBOARDING_RATE_LIMITED", Assert.Single(results, x => !x.IsSuccess).Error!.Code);
        Assert.Equal(10L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions"));
    }

    [BillingPostgresFact]
    public async Task Restricted_runtime_creates_verified_trainee_without_password_or_automatic_session()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        await using var context = db.Context();
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
        var service = Service(context);
        var proof = await service.BeginAsync(new("new-google", " NEW@EXAMPLE.TEST "), default);
        Assert.True(proof.IsSuccess, proof.Error?.Message);
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.NotEqual(proof.Value!.Token, await db.Scalar("SELECT onboarding_token_hash FROM auth_google_onboarding_sessions"));
        var result = await service.CompleteAsync(Trainee(proof.Value.Token, " GOOGLE_User "), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("google_user", result.Value!.User.Username);
        Assert.Equal("new@example.test", result.Value.User.Email);
        Assert.NotNull(result.Value.User.EmailVerifiedAt);
        Assert.Null(result.Value.User.OrganizationId);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid='new-google' AND password_hash IS NULL"));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs WHERE action='Create'"));
    }

    [BillingPostgresFact]
    public async Task Organization_and_owner_are_created_atomically_and_same_input_replays()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        var token = await Begin(db);
        var request = new GoogleOnboardingCompleteRequest(token, UserRole.OrganizationUser,
            OrganizationName: " My Org ", OrganizationAddress: "1 Example Street", OrganizationPhoneNumber: "070 636 4866");
        await using var context = db.Context();
        var first = await Service(context).CompleteAsync(request, default);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.False(first.Value!.Replayed); Assert.NotNull(first.Value.User.OrganizationId); Assert.Null(first.Value.User.Username);
        var second = await Service(context).CompleteAsync(request with { OrganizationName = "My Org" }, default);
        Assert.True(second.Value?.Replayed);
        Assert.Equal(first.Value.User.Id, second.Value!.User.Id);
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM organizations"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        var different = await Service(context).CompleteAsync(request with { OrganizationName = "Different" }, default);
        Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", different.Error?.Code);
    }

    [BillingPostgresFact]
    public async Task Concurrent_completions_commit_only_one_account_and_receipt()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); var token = await Begin(db);
        async Task<AuthResult<GoogleOnboardingCompletion>> Complete()
        {
            await using var context = db.Context(); return await Service(context).CompleteAsync(Trainee(token), default);
        }
        var results = await Task.WhenAll(Complete(), Complete());
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        Assert.Single(results, x => !x.Value!.Replayed); Assert.Single(results, x => x.Value!.Replayed);
        Assert.Equal(results[0].Value!.User.Id, results[1].Value!.User.Id);
        Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Separate_proofs_for_same_uid_cannot_create_multiple_accounts()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        var one = await Begin(db); var two = await Begin(db);
        await using var context = db.Context();
        Assert.True((await Service(context).CompleteAsync(Trainee(one), default)).IsSuccess);
        Assert.Equal("ACCOUNT_CHANGED", (await Service(context).CompleteAsync(Trainee(two, "other_user"), default)).Error?.Code);
        Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM users"));
    }

    [BillingPostgresFact]
    public async Task Invalid_fields_admin_type_and_expired_proof_do_not_create_accounts()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); var token = await Begin(db);
        await using var context = db.Context(); var service = Service(context);
        var admin = await service.CompleteAsync(Trainee(token) with { AccountType = UserRole.PlatformAdmin }, default);
        Assert.Contains("accountType", admin.Error!.Errors!.Keys);
        var dob = await service.CompleteAsync(Trainee(token) with { Dob = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)) }, default);
        Assert.Contains("dob", dob.Error!.Errors!.Keys);
        Assert.Equal("INVALID_ONBOARDING_TOKEN", (await service.CompleteAsync(Trainee("invalid"), default)).Error?.Code);
        await db.Sql("UPDATE auth_google_onboarding_sessions SET created_at=now()-interval '20 minutes',expires_at=now()-interval '5 minutes'");
        Assert.Equal("INVALID_ONBOARDING_TOKEN", (await service.CompleteAsync(Trainee(token), default)).Error?.Code);
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
    }

    [BillingPostgresFact]
    public async Task Email_collision_after_exchange_requires_explicit_link_and_preserves_receipt()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); var token = await Begin(db);
        await db.Sql($"UPDATE users SET email=' NEW@EXAMPLE.TEST ' WHERE id='{BillingDatabase.Owner}'");
        await using var context = db.Context();
        Assert.Equal("ACCOUNT_LINK_REQUIRED", (await Service(context).CompleteAsync(Trainee(token), default)).Error?.Code);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE completed_at IS NOT NULL"));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
    }

    [BillingPostgresFact]
    public async Task Local_registration_winning_between_lookup_and_insert_returns_link_required()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); var token = await Begin(db);
        await using var context = db.Context(); var store = new AuthStore(context);
        async Task<RegisterConflict> Register(object?[] args)
        {
            // A second connection commits a local registration after onboarding checked the email.
            await db.Sql("INSERT INTO users(id,email,role,username,is_active,created_at,updated_at,password_hash) VALUES(gen_random_uuid(),'new@example.test','Trainee','race_local_user',true,now(),now(),'fake-local-hash')");
            return await store.TryCreateTraineeAsync((User)args[0]!, (CancellationToken)args[1]!);
        }
        var interleaved = ResetProxy.For<IAuthStore>((method, args) => method == nameof(IAuthStore.TryCreateTraineeAsync)
            ? Register(args) : typeof(IAuthStore).GetMethod(method)!.Invoke(store, args)!);
        var result = await new GoogleOnboardingService(context, interleaved, TimeProvider.System).CompleteAsync(Trainee(token), default);
        Assert.Equal("ACCOUNT_LINK_REQUIRED", result.Error?.Code);
        Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid IS NOT NULL"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE completed_at IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Username_conflict_and_audit_failure_rollback_without_orphaned_organization()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); var token = await Begin(db);
        await db.Sql($"UPDATE users SET username='google_user' WHERE id='{BillingDatabase.Owner}'");
        await using (var context = db.Context())
            Assert.Equal("USERNAME_EXISTS", (await Service(context).CompleteAsync(Trainee(token), default)).Error?.Code);
        await db.Sql("ALTER TABLE audit_logs ADD CONSTRAINT reject_google_audit CHECK(false) NOT VALID");
        await using (var context = db.Context())
            await Assert.ThrowsAsync<DbUpdateException>(() => Service(context).CompleteAsync(new(token, UserRole.OrganizationUser,
                OrganizationName: "Org", OrganizationAddress: "Address", OrganizationPhoneNumber: "0706364866"), default));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users")); Assert.Equal(2L, await db.Scalar("SELECT count(*) FROM organizations"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE completed_at IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Replay_survives_initial_expiry_but_is_limited_to_24_hours()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); var token = await Begin(db);
        await using var context = db.Context(); var service = Service(context);
        Assert.True((await service.CompleteAsync(Trainee(token), default)).IsSuccess);
        await db.Sql("UPDATE auth_google_onboarding_sessions SET created_at=now()-interval '1 hour',expires_at=now()-interval '45 minutes',completed_at=now()-interval '50 minutes'");
        Assert.True((await service.CompleteAsync(Trainee(token), default)).Value?.Replayed);
        await db.Sql("UPDATE auth_google_onboarding_sessions SET created_at=now()-interval '26 hours',expires_at=now()-interval '25 hours',completed_at=now()-interval '25 hours'");
        Assert.Equal("INVALID_ONBOARDING_TOKEN", (await service.CompleteAsync(Trainee(token), default)).Error?.Code);
    }
}
