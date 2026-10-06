using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleOnboardingSessionPostgresTests
{
    [BillingPostgresFact]
    public async Task Legacy_proof_and_completed_receipt_survive_upgrade_without_reissuing_session()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db, includeDisplayName: false);
        var proof = new string('l', 43);
        var proofHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(proof)));
        await db.Sql($"INSERT INTO auth_google_onboarding_sessions(id,firebase_uid,email,onboarding_token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'legacy-uid','legacy@example.test','{proofHash}',now(),now()+interval '15 minutes')");
        await BackendDatabasePermissionsTests.Apply(db, "AddGoogleOnboardingDisplayName");
        var request = new GoogleOnboardingCompleteRequest(proof, UserRole.Trainee, "legacy_user");
        await using var context = db.Context();
        var service = new GoogleOnboardingService(context, new AuthStore(context), TimeProvider.System, GoogleOnboardingTestDoubles.Tokens());
        var completed = await service.CompleteAsync(request, default);
        Assert.True(completed.IsSuccess, completed.Error?.Message);
        Assert.Null(completed.Value!.Authentication.User.FullName);
        // Previous deployment wrote the same numeric-enum canonical hash, without any bearer receipt.
        var legacyInput = GoogleAuthRules.Validate(request, DateOnly.FromDateTime(DateTime.UtcNow)).Value!;
        var legacyHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(legacyInput))));
        Assert.Equal(legacyHash, await db.Scalar("SELECT completed_input_hash FROM auth_google_onboarding_sessions"));
        var replay = await service.CompleteAsync(request, default);
        Assert.Equal("ONBOARDING_ALREADY_COMPLETED", replay.Error?.Code);
        Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
    }

    [BillingPostgresFact]
    public async Task Signing_failure_rolls_back_and_proof_can_be_used_on_retry()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        var tokens = GoogleOnboardingTestDoubles.Tokens();
        var failing = ResetProxy.For<ITokenService>((method, args) => method == nameof(ITokenService.CreateAccessToken)
            ? throw new InvalidOperationException("Injected signing failure") : typeof(ITokenService).GetMethod(method)!.Invoke(tokens, args)!);
        string proof;
        await using (var context = db.Context())
        {
            var service = new GoogleOnboardingService(context, new AuthStore(context), TimeProvider.System, failing);
            proof = (await service.BeginAsync(new("sign-uid", "sign@example.test"), default)).Value!.Token;
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync(new(proof, UserRole.Trainee, "sign_user"), default));
        }
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        await using var retryContext = db.Context();
        var retry = new GoogleOnboardingService(retryContext, new AuthStore(retryContext), TimeProvider.System, tokens);
        Assert.True((await retry.CompleteAsync(new(proof, UserRole.Trainee, "sign_user"), default)).IsSuccess);
    }

    [BillingPostgresFact]
    public async Task Uid_lock_timeout_is_retryable_and_does_not_consume_proof()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        await using var context = db.Context();
        var service = new GoogleOnboardingService(context, new AuthStore(context), TimeProvider.System, GoogleOnboardingTestDoubles.Tokens());
        var proof = (await service.BeginAsync(new("busy-uid", "busy@example.test"), default)).Value!;
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('fire3d:google:busy-uid',0))", connection, transaction).ExecuteNonQueryAsync();
        var result = await service.CompleteAsync(new(proof.Token, UserRole.Trainee, "busy_user"), default);
        Assert.Equal("ONBOARDING_RETRY_REQUIRED", result.Error?.Code);
        Assert.Equal(503, result.Error?.Status); Assert.Equal(1, result.Error?.RetryAfterSeconds);
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE completed_at IS NOT NULL"));
        await transaction.RollbackAsync();
        Assert.True((await service.CompleteAsync(new(proof.Token, UserRole.Trainee, "busy_user"), default)).IsSuccess);
    }

    [BillingPostgresFact]
    public async Task First_completion_issues_session_and_replay_does_not_issue_again()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        await using var context = db.Context(); var service = new GoogleOnboardingService(context, new AuthStore(context), TimeProvider.System, GoogleOnboardingTestDoubles.Tokens());
        var proof = await service.BeginAsync(new("session-uid", "session@example.test", "Google Display Name"), default);
        var request = new GoogleOnboardingCompleteRequest(proof.Value!.Token, UserRole.Trainee, "session_user");
        var first = await service.CompleteAsync(request, default);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(first.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var authentication = json.RootElement.GetProperty("authentication");
        Assert.False(string.IsNullOrWhiteSpace(authentication.GetProperty("accessToken").GetString()));
        Assert.Equal("Google Display Name", authentication.GetProperty("user").GetProperty("fullName").GetString());
        var replay = await service.CompleteAsync(request, default);
        Assert.Equal("ONBOARDING_ALREADY_COMPLETED", replay.Error?.Code); Assert.Equal(409, replay.Error?.Status);
        Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
        Assert.Equal(2L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Refresh_write_failure_rolls_back_account_organization_receipt_and_audit()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        await db.Sql("ALTER TABLE auth_refresh_tokens ADD CONSTRAINT reject_new_sessions CHECK(false) NOT VALID");
        await using var context = db.Context(); var service = new GoogleOnboardingService(context, new AuthStore(context), TimeProvider.System, GoogleOnboardingTestDoubles.Tokens());
        var proof = await service.BeginAsync(new("rollback-uid", "rollback@example.test"), default);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.CompleteAsync(new(proof.Value!.Token, UserRole.OrganizationUser,
            OrganizationName: "Org", OrganizationAddress: "Address", OrganizationPhoneNumber: "0706364866"), default));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(2L, await db.Scalar("SELECT count(*) FROM organizations"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE completed_at IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Login_audit_failure_rolls_back_the_session_already_written()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        await db.Sql("ALTER TABLE audit_logs ADD CONSTRAINT reject_login_audit CHECK(action <> 'Login') NOT VALID");
        await using var context = db.Context(); var service = new GoogleOnboardingService(context, new AuthStore(context), TimeProvider.System, GoogleOnboardingTestDoubles.Tokens());
        var proof = await service.BeginAsync(new("audit-uid", "audit@example.test"), default);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.CompleteAsync(new(proof.Value!.Token, UserRole.Trainee, "audit_user"), default));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE completed_at IS NOT NULL"));
    }

    [BillingPostgresFact]
    public async Task Missing_and_expired_proofs_have_distinct_errors()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db);
        await using var context = db.Context(); var service = new GoogleOnboardingService(context, new AuthStore(context), TimeProvider.System, GoogleOnboardingTestDoubles.Tokens());
        Assert.Equal("ONBOARDING_TOKEN_INVALID", (await service.CompleteAsync(new("bad", UserRole.Trainee, "user_name"), default)).Error?.Code);
        var proof = await service.BeginAsync(new("expired-uid", "expired@example.test"), default);
        await db.Sql("UPDATE auth_google_onboarding_sessions SET created_at=now()-interval '20 minutes',expires_at=now()-interval '5 minutes'");
        Assert.Equal("ONBOARDING_TOKEN_EXPIRED", (await service.CompleteAsync(new(proof.Value!.Token, UserRole.Trainee, "user_name"), default)).Error?.Code);
    }
}
