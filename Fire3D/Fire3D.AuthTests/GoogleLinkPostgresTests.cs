using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleLinkPostgresTests
{
    internal const string Password = "local-password";
    internal static readonly PasswordService Passwords = new();
    internal static async Task Prepare(BillingDatabase db)
    {
        await GoogleOnboardingPostgresTests.Prepare(db);
        await using var context = db.Context(); var store = new AuthStore(context);
        foreach (var id in new[] { BillingDatabase.Owner, BillingDatabase.Other })
        {
            var user = await store.FindUserAsync(id, default);
            await store.UpdatePasswordHashAsync(id, Passwords.Hash(user!, Password), DateTime.UtcNow, default);
        }
        await db.Sql($$"""
            CREATE TABLE local_password_reset_tokens(id uuid PRIMARY KEY,user_id uuid REFERENCES users(id),token_hash text,
                expires_at timestamptz,used_at timestamptz);
            CREATE TABLE password_reset_operations(user_id uuid,status text,finished_at timestamptz);
            INSERT INTO local_password_reset_tokens VALUES(gen_random_uuid(),'{{BillingDatabase.Owner}}','test-hash',now()+interval '1 hour',null);
            INSERT INTO password_reset_tokens(id,user_id,expires_at) VALUES(gen_random_uuid(),'{{BillingDatabase.Owner}}',now()+interval '1 hour');
            GRANT SELECT,UPDATE ON local_password_reset_tokens,auth_refresh_tokens TO fire3d_api;
            GRANT SELECT ON password_reset_operations TO fire3d_api;
            REVOKE ALL ON password_reset_tokens FROM PUBLIC,fire3d_api;
            ALTER TABLE password_reset_tokens ENABLE ROW LEVEL SECURITY;
            """);
        await BackendDatabasePermissionsTests.Apply(db, "AddPasswordRecoveryGate");
    }
    internal static IIdentityProvider Provider(Func<Task<VerifiedIdentity>>? verify = null) => ResetProxy.For<IIdentityProvider>((_, _) =>
        verify is null ? Task.FromResult(new VerifiedIdentity("linked-google", "different-google@example.test")) : verify());
    private static GoogleLinkService Service(Fire3DDbContext context, IIdentityProvider? provider = null) =>
        new(context, new AuthStore(context), Passwords, provider ?? Provider(), TimeProvider.System);
    private static Task<AuthResult<GoogleLinkResponse>> Link(Fire3DDbContext context, IIdentityProvider? provider = null,
        Guid? actor = null, Guid? family = null, string password = Password) => Service(context, provider).LinkAsync(
            actor ?? BillingDatabase.Owner, family ?? BillingDatabase.Owner, new("fake-google-id-token", password), default);

    [BillingPostgresFact]
    public async Task Restricted_runtime_links_without_changing_local_identity_and_revokes_proofs_atomically()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        await using var context = db.Context();
        await context.Database.OpenConnectionAsync(); await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
        var before = await new AuthStore(context).FindUserAsync(BillingDatabase.Owner, default);
        var result = await Link(context); Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value!.AlreadyLinked); Assert.True(result.Value.RequiresLogin);
        Assert.Equal(before!.Role, result.Value.User.Role); Assert.Equal(before.OrganizationId, result.Value.User.OrganizationId);
        Assert.Equal(before.Email, result.Value.User.Email); Assert.Equal(before.ProfileRevision + 1, result.Value.User.ProfileRevision);
        Assert.Equal("linked-google", await db.Scalar($"SELECT firebase_uid FROM users WHERE id='{BillingDatabase.Owner}'"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM local_password_reset_tokens WHERE used_at IS NOT NULL"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM password_reset_tokens WHERE used_at IS NOT NULL"));
        Assert.Equal(0L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Owner}' AND revoked_at IS NULL"));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Other}' AND revoked_at IS NULL"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs WHERE action='Update'"));
    }

    [BillingPostgresFact]
    public async Task Wrong_password_never_contacts_provider()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        var provider = ResetProxy.For<IIdentityProvider>((_, _) => throw new Exception("Must not contact provider"));
        Assert.Equal("INVALID_CURRENT_PASSWORD", (await Link(context, provider, password: "wrong")).Error?.Code);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Family_revoked_during_provider_wait_cannot_link()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        var provider = Provider(async () => { await db.Sql($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{BillingDatabase.Owner}'"); return new("linked-google", "google@example.test"); });
        Assert.Equal(401, (await Link(context, provider)).Error?.Status);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Password_change_during_provider_wait_invalidates_local_proof()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        var provider = Provider(async () => { await db.Sql($"UPDATE users SET password_hash='changed-snapshot' WHERE id='{BillingDatabase.Owner}'"); return new("linked-google", "google@example.test"); });
        Assert.Equal("ACCOUNT_CHANGED", (await Link(context, provider)).Error?.Code);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Lifecycle_changes_and_pending_local_verification_are_rechecked()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        var provider = Provider(async () => { await db.Sql($"UPDATE organizations SET is_active=false WHERE id='{BillingDatabase.Org}'"); return new("linked-google", "google@example.test"); });
        Assert.Equal(401, (await Link(context, provider)).Error?.Status);
        await db.Sql($"UPDATE organizations SET is_active=true WHERE id='{BillingDatabase.Org}'; UPDATE users SET registration_expires_at=now()+interval '1 hour' WHERE id='{BillingDatabase.Owner}'");
        Assert.Equal("EMAIL_NOT_VERIFIED", (await Link(context)).Error?.Code);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Other_owner_or_replacement_is_rejected_without_changing_bindings()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        await db.Sql($"UPDATE users SET firebase_uid='linked-google' WHERE id='{BillingDatabase.Other}'");
        Assert.Equal("GOOGLE_ALREADY_LINKED", (await Link(context)).Error?.Code);
        await db.Sql($"UPDATE users SET firebase_uid='my-previous-google' WHERE id='{BillingDatabase.Owner}'; UPDATE users SET firebase_uid=null WHERE id='{BillingDatabase.Other}'");
        Assert.Equal("GOOGLE_ACCOUNT_REPLACEMENT_FORBIDDEN", (await Link(context)).Error?.Code);
        Assert.Equal("my-previous-google", await db.Scalar($"SELECT firebase_uid FROM users WHERE id='{BillingDatabase.Owner}'"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Concurrent_accounts_cannot_claim_the_same_google_uid()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        async Task<AuthResult<GoogleLinkResponse>> Claim(Guid id)
        {
            await using var context = db.Context(); return await Link(context, actor: id, family: id);
        }
        var results = await Task.WhenAll(Claim(BillingDatabase.Owner), Claim(BillingDatabase.Other));
        Assert.Single(results, x => x.IsSuccess);
        Assert.Equal("GOOGLE_ALREADY_LINKED", Assert.Single(results, x => !x.IsSuccess).Error?.Code);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid='linked-google'"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Link_and_onboarding_compete_without_duplicating_google_identity()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        await using var proofContext = db.Context();
        var proof = await new GoogleOnboardingService(proofContext, new AuthStore(proofContext), TimeProvider.System)
            .BeginAsync(new("linked-google", "different-google@example.test"), default);
        Assert.True(proof.IsSuccess);
        await using var linkContext = db.Context(); await using var completeContext = db.Context();
        var linkTask = Link(linkContext);
        var completeTask = new GoogleOnboardingService(completeContext, new AuthStore(completeContext), TimeProvider.System)
            .CompleteAsync(new(proof.Value!.Token, Fire3D.Domain.Enums.UserRole.Trainee, "race_google_user"), default);
        await Task.WhenAll(linkTask, completeTask);
        Assert.NotEqual(linkTask.Result.IsSuccess, completeTask.Result.IsSuccess);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid='linked-google'"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Google_only_account_cannot_use_link_to_bypass_local_password_proof()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        await db.Sql($"UPDATE users SET password_hash=null WHERE id='{BillingDatabase.Owner}'");
        await using var context = db.Context();
        Assert.Equal("LOCAL_PASSWORD_REQUIRED", (await Link(context)).Error?.Code);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Audit_failure_rolls_back_uid_revision_reset_proofs_and_sessions()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        var before = await db.Scalar($"SELECT profile_revision FROM users WHERE id='{BillingDatabase.Owner}'");
        await db.Sql("ALTER TABLE audit_logs ADD CONSTRAINT reject_google_link_audit CHECK(false) NOT VALID");
        await using (var context = db.Context()) await Assert.ThrowsAsync<DbUpdateException>(() => Link(context));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid IS NOT NULL"));
        Assert.Equal(before, await db.Scalar($"SELECT profile_revision FROM users WHERE id='{BillingDatabase.Owner}'"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM local_password_reset_tokens WHERE used_at IS NULL"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM password_reset_tokens WHERE used_at IS NULL"));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Owner}' AND revoked_at IS NULL"));
    }

    [BillingPostgresFact]
    public async Task Replay_requires_a_fresh_session_and_does_not_repeat_audit_or_revoke_it()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        var first = await Link(context); Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(401, (await Link(context)).Error?.Status);
        var family = Guid.NewGuid();
        await db.Sql($"INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{BillingDatabase.Owner}','{family}','fresh-link-replay',now(),now()+interval '1 hour')");
        var replay = await Link(context, family: family);
        Assert.True(replay.IsSuccess, replay.Error?.Message); Assert.True(replay.Value!.AlreadyLinked); Assert.False(replay.Value.RequiresLogin);
        Assert.Equal(first.Value!.User.ProfileRevision, replay.Value.User.ProfileRevision);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE family_id='{family}' AND revoked_at IS NULL"));
    }

    [BillingPostgresFact]
    public async Task Pending_reset_blocks_new_binding()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        await db.Sql($"INSERT INTO password_reset_operations VALUES('{BillingDatabase.Owner}','Pending',null)");
        Assert.Equal("PASSWORD_RESET_PENDING", (await Link(context)).Error?.Code);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM users WHERE firebase_uid IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Invalid_google_and_provider_outage_have_distinct_errors()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db); await using var context = db.Context();
        foreach (var failure in new[] { GoogleIdentityFailure.InvalidToken, GoogleIdentityFailure.ProviderUnavailable })
        {
            var provider = ResetProxy.For<IIdentityProvider>((_, _) => throw new GoogleIdentityException(failure));
            var error = (await Link(context, provider)).Error;
            Assert.Equal(failure == GoogleIdentityFailure.InvalidToken ? 401 : 503, error?.Status);
            Assert.Equal(failure == GoogleIdentityFailure.InvalidToken ? "INVALID_FIREBASE_TOKEN" : "GOOGLE_PROVIDER_UNAVAILABLE", error?.Code);
        }
        using var cts = new CancellationTokenSource();
        var cancelProvider = ResetProxy.For<IIdentityProvider>((_, _) => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(context, cancelProvider).LinkAsync(BillingDatabase.Owner,
            BillingDatabase.Owner, new("fake", Password), cts.Token));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }
}
