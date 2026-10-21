using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Security.Cryptography;
using Fire3D.API.Authorization;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Users.Commands.RegisterDevice;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AuthInfrastructureGapTests
{
    private static readonly string ValidKey = Convert.ToBase64String(new byte[32]);

    [Theory]
    [InlineData(0, 7)]
    [InlineData(61, 7)]
    [InlineData(15, 0)]
    [InlineData(15, 31)]
    public void Jwt_options_reject_lifetimes_outside_the_supported_range(int accessMinutes, int refreshDays) =>
        Assert.False(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = ValidKey, AccessTokenMinutes = accessMinutes, RefreshTokenDays = refreshDays }.IsValid());

    [Fact]
    public void Jwt_options_require_a_base64_key_of_at_least_32_bytes()
    {
        Assert.True(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = ValidKey }.IsValid());
        Assert.False(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = "not base64!" }.IsValid());
        Assert.False(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = Convert.ToBase64String(new byte[31]) }.IsValid());
        Assert.False(new JwtOptions { Issuer = " ", Audience = "a", SigningKey = ValidKey }.IsValid());
    }

    [Fact]
    public void Password_service_treats_a_malformed_stored_hash_as_a_failed_verification()
    {
        var passwords = new PasswordService();
        Assert.False(passwords.Verify(new User { PasswordHash = "not-a-password-hash!" }, "secret", out var rehash));
        Assert.False(rehash);
        Assert.False(passwords.Verify(new User(), "secret", out _));
        var user = new User();
        user.PasswordHash = passwords.Hash(user, "secret");
        Assert.True(passwords.Verify(user, "secret", out _));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(366, 10)]
    [InlineData(30, 0)]
    [InlineData(30, 10001)]
    public async Task Refresh_cleanup_rejects_out_of_range_retention_or_batch_before_querying(int retentionDays, int batchSize)
    {
        // The context points at a closed port: any database access would fail differently.
        await using var db = new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Timeout=1").Options);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new RefreshTokenCleanupStore(db).DeleteExpiredFamiliesAsync(retentionDays, batchSize, default));
    }

    [Fact]
    public void Current_actor_organization_is_optional_and_must_be_a_guid()
    {
        var organization = Guid.NewGuid();
        Assert.Equal(organization, new ClaimsPrincipal(new ClaimsIdentity([new Claim("organization_id", organization.ToString())])).GetOrganizationId());
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity([new Claim("organization_id", "acme")])).GetOrganizationId());
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity()).GetOrganizationId());
    }

    [Fact]
    public async Task Device_commands_reject_an_account_that_became_unavailable_inside_the_lock()
    {
        var transaction = ResetProxy.For<IAuthTransaction>((method, _) => method == "DisposeAsync" ? ValueTask.CompletedTask : throw new InvalidOperationException("Must not commit"));
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult(transaction),
            "FindUserAsync" => Task.FromResult<User?>(null),
            _ => throw new InvalidOperationException("Unexpected: " + method)
        });
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var device = Guid.NewGuid().ToString("D");
        var register = await new RegisterDeviceCommandHandler(store, TimeProvider.System)
            .Handle(new(Guid.NewGuid(), Guid.NewGuid(), device, key, "token", null, null, null), default);
        Assert.Equal(("UNAUTHORIZED", 401), (register.Error?.Code, register.Error?.Status));
        var revoke = await new RevokeDeviceCommandHandler(store, TimeProvider.System).Handle(new(Guid.NewGuid(), Guid.NewGuid(), device, key), default);
        Assert.Equal(("UNAUTHORIZED", 401), (revoke.Error?.Code, revoke.Error?.Status));
    }

    [Fact]
    public async Task Organization_registration_requires_an_email_proof_once_the_form_is_valid()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected: " + method));
        var otp = ResetProxy.For<IRegistrationOtpService>((method, _) => throw new InvalidOperationException("Unexpected: " + method));
        var result = await new Fire3D.Application.Authentication.Commands.SelfRegistration.RegisterOrganizationCommandHandler(store, new PasswordService(), otp, TimeProvider.System)
            .Handle(new("owner@example.test", "Strong-Password-2026!", "Strong-Password-2026!", "Owner", "Org", "1 Example Street", "0901234567"), default);
        Assert.Equal(("EMAIL_VERIFICATION_REQUIRED", 400), (result.Error?.Code, result.Error?.Status));
    }

    [Fact]
    public async Task Logout_all_without_an_identity_or_session_is_unauthorized_before_the_store()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected: " + method));
        var handler = new Fire3D.Application.Authentication.Commands.Logout.LogoutAllCommandHandler(store, TimeProvider.System);
        Assert.Equal(401, (await handler.Handle(new(Guid.Empty, Guid.NewGuid()), default)).Error?.Status);
        Assert.Equal(401, (await handler.Handle(new(Guid.NewGuid(), Guid.Empty), default)).Error?.Status);
    }

    [Fact]
    public void Avatar_signature_check_rejects_an_unsupported_declared_type_even_with_png_bytes() =>
        Assert.Equal("INVALID_AVATAR_CONTENT", Fire3D.Application.Authentication.Avatar.AvatarUploadRules
            .ValidateImageSignature("image/gif", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0 })?.Code);

    [Fact]
    public void Add_database_requires_a_default_connection_string()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDatabase(new ConfigurationBuilder().Build()));
        Assert.Contains("DefaultConnection", error.Message);
    }

    [Fact]
    public async Task Readiness_reports_an_unreachable_database_without_connection_details()
    {
        var services = new ServiceCollection();
        services.AddDbContext<Fire3DDbContext>(options => options.UseNpgsql("Host=127.0.0.1;Port=1;Timeout=1;Pooling=false"));
        await using var provider = services.BuildServiceProvider();
        var result = await new DatabaseReadinessCheck(provider.GetRequiredService<IServiceScopeFactory>()).CheckHealthAsync(new HealthCheckContext(), default);
        Assert.Equal((HealthStatus.Unhealthy, "DATABASE_UNREACHABLE"), (result.Status, result.Description));
        Assert.Empty(result.Data);
    }

    [BillingPostgresFact]
    public async Task Readiness_reports_only_the_exception_type_when_the_migration_check_fails()
    {
        var services = new ServiceCollection();
        services.AddDbContext<Fire3DDbContext>(options => options
            .UseNpgsql(Environment.GetEnvironmentVariable("FET3D_BILLING_TEST_ADMIN"))
            .AddInterceptors(new FailMigrationHistory()));
        await using var provider = services.BuildServiceProvider();
        var result = await new DatabaseReadinessCheck(provider.GetRequiredService<IServiceScopeFactory>()).CheckHealthAsync(new HealthCheckContext(), default);
        Assert.Equal((HealthStatus.Unhealthy, "DATABASE_CHECK_FAILED"), (result.Status, result.Description));
        Assert.Equal(nameof(InvalidOperationException), result.Data["error"]);
    }

    [BillingPostgresFact]
    public async Task Database_timing_interceptor_is_opt_in_and_records_each_command_kind()
    {
        var kinds = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == "FET3D.Database") l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            Assert.True(value >= 0);
            lock (kinds) kinds.AddRange(tags.ToArray().Where(t => t.Key == "kind").Select(t => (string)t.Value!));
        });
        listener.Start();

        async Task RunCommands(bool enabled)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = Environment.GetEnvironmentVariable("FET3D_BILLING_TEST_ADMIN"),
                ["AuthDiagnostics:DatabaseTimingEnabled"] = enabled.ToString()
            }).Build());
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync());
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            var command = db.GetService<IRawSqlCommandBuilder>().Build("SELECT 1");
            Assert.Equal(1, await command.ExecuteScalarAsync(new RelationalCommandParameterObject(
                db.GetService<IRelationalConnection>(), null, null, db, db.GetService<IRelationalCommandDiagnosticsLogger>())));
        }

        await RunCommands(false);
        Assert.Empty(kinds);
        await RunCommands(true);
        Assert.Equal(["nonquery", "reader", "scalar"], kinds.Distinct().Order());
    }

    private sealed class FailMigrationHistory : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("__EFMigrationsHistory", StringComparison.Ordinal)
                ? throw new InvalidOperationException("history unavailable") : ValueTask.FromResult(result);
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("__EFMigrationsHistory", StringComparison.Ordinal)
                ? throw new InvalidOperationException("history unavailable") : ValueTask.FromResult(result);
    }
}
