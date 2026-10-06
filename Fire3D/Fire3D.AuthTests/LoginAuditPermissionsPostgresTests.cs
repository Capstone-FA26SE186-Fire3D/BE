using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class LoginAuditPermissionsPostgresTests
{
    private const string Password = "LoginTest123456";

    [BillingPostgresFact]
    public Task Login_can_repeat_and_issue_usable_sessions_without_reading_audit_rows() => WithRestrictedApi(async (db, client) =>
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", Password));
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = body.RootElement.GetProperty("refreshToken").GetString() });
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        }
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM audit_logs WHERE action='Login'"));
        Assert.Equal(3L, await db.Scalar($"SELECT count(DISTINCT family_id) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Admin}'"));
        Assert.Equal(true, await db.Scalar($"SELECT last_login_at IS NOT NULL FROM users WHERE id='{BillingDatabase.Admin}'"));
    });

    [BillingPostgresFact]
    public Task Audit_failure_rolls_back_login_and_session_with_insert_only_permissions() => WithRestrictedApi(async (db, client) =>
    {
        await db.Sql("ALTER TABLE audit_logs ADD CONSTRAINT reject_login_audit CHECK(action<>'Login') NOT VALID");
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", Password));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.RootElement.GetProperty("sqlState").GetString());
        Assert.Equal(0L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Admin}'"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(true, await db.Scalar($"SELECT last_login_at IS NULL FROM users WHERE id='{BillingDatabase.Admin}'"));
    });

    [BillingPostgresFact]
    public Task Pending_password_reset_still_blocks_session_without_audit_read_permissions() => WithRestrictedApi(async (db, client) =>
    {
        await db.Sql($"INSERT INTO password_reset_operations(id,user_id,firebase_uid,code_hash,status) VALUES(gen_random_uuid(),'{BillingDatabase.Admin}','test',repeat('a',64),'Pending')");
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", Password));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("RESET_PENDING", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0L, await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Admin}'"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(true, await db.Scalar($"SELECT last_login_at IS NULL FROM users WHERE id='{BillingDatabase.Admin}'"));
    });

    private static async Task WithRestrictedApi(Func<BillingDatabase, HttpClient, Task> test)
    {
        var role = "fet3d_login_test_" + Guid.NewGuid().ToString("N");
        var adminConnection = Environment.GetEnvironmentVariable("FET3D_BILLING_TEST_ADMIN")!;
        // BillingDatabase validates loopback before any write; never use application User Secrets here.
        var roleCreated = false;
        try
        {
            await using var db = await BillingDatabase.Create(false);
            await db.Sql(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "002_password_reset_recovery.sql")));
            await db.Sql($"CREATE ROLE {role} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT");
            roleCreated = true;
            await db.Sql($"""
                GRANT USAGE ON SCHEMA public TO {role};
                GRANT SELECT,UPDATE ON users TO {role};
                GRANT SELECT ON organizations,password_reset_operations TO {role};
                GRANT SELECT,INSERT,UPDATE ON auth_refresh_tokens TO {role};
                REVOKE ALL ON audit_logs FROM PUBLIC;
                GRANT INSERT ON audit_logs TO {role};
                ALTER TABLE audit_logs ENABLE ROW LEVEL SECURITY;
                CREATE POLICY login_audit_insert ON audit_logs FOR INSERT TO {role} WITH CHECK(true);
                CREATE POLICY login_reset_read ON password_reset_operations FOR SELECT TO {role} USING(true);
                DELETE FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Admin}';
                """);
            Assert.Equal(false, await db.Scalar($"SELECT has_column_privilege('{role}','public.audit_logs','id','SELECT')"));
            Assert.Equal(true, await db.Scalar($"SELECT has_table_privilege('{role}','public.audit_logs','INSERT')"));
            await using (var context = db.Context())
            {
                var hash = new PasswordService().Hash(new User { Id = BillingDatabase.Admin }, Password);
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET password_hash={hash},email_verified_at=now() WHERE id={BillingDatabase.Admin}");
            }
            var connection = new NpgsqlConnectionStringBuilder(db.Connection) { Username = role }.ConnectionString;
            using var factory = new BillingApiTests.Factory(db).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<Fire3DDbContext>();
                services.RemoveAll<DbContextOptions<Fire3DDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<Fire3DDbContext>>();
                services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:DefaultConnection"] = connection }).Build());
                services.PostConfigure<AuthenticationOptions>(options =>
                { options.DefaultAuthenticateScheme = "Bearer"; options.DefaultChallengeScheme = "Bearer"; });
                services.AddExceptionHandler<TestDatabaseFailureHandler>();
                services.RemoveAll<IStorageService>();
                services.AddSingleton(ResetProxy.For<IStorageService>((_, _) => throw new InvalidOperationException("Login tests must not call S3")));
            }));
            using var client = factory.CreateClient();
            await test(db, client);
        }
        finally
        {
            if (roleCreated)
            {
                await using var connection = new NpgsqlConnection(adminConnection);
                await connection.OpenAsync();
                await new NpgsqlCommand($"DROP ROLE {role}", connection).ExecuteNonQueryAsync();
            }
        }
    }

    // Fixture diagnostics expose only safe SQL state/message, never parameters or failing row details.
    public sealed class TestDatabaseFailureHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception error, CancellationToken ct)
        {
            var root = error.GetBaseException();
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new
            {
                failureKind = root.GetType().Name,
                sqlState = (root as PostgresException)?.SqlState,
                message = (root as PostgresException)?.MessageText
            }, ct);
            return true;
        }
    }
}
