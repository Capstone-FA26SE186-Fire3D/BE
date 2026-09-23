using System.IdentityModel.Tokens.Jwt;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.BootstrapAdmin;
using MediatR;
using Fire3D.API.Extensions;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIRE3D_TEST_ADMIN_CONNECTION"))
            && Environment.GetEnvironmentVariable("FIRE3D_TEST_USE_LOCAL_SECRETS") != "1")
            Skip = "Set FIRE3D_TEST_ADMIN_CONNECTION (local test server with CREATEDB), or explicitly opt in to API User Secrets with FIRE3D_TEST_USE_LOCAL_SECRETS=1.";
    }
}

// Every test gets a disposable database; application databases are never migrated or cleared.
public sealed partial class AuthIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string databaseName = "fire3d_auth_test_" + Guid.NewGuid().ToString("N");
    private readonly string signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    private string adminConnection = null!;
    private string testConnection = null!;
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;
    private bool created;
    private FirebaseAdmin.FirebaseApp? ownedFirebaseApp;
    private Guid adminId;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("FIRE3D_TEST_ADMIN_CONNECTION");
        if (string.IsNullOrEmpty(connection))
            connection = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly)
                .Build().GetConnectionString("DefaultConnection");
        var builder = new NpgsqlConnectionStringBuilder(connection)
        {
            Database = "postgres", Pooling = false, IncludeErrorDetail = false
        };
        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Integration tests only accept a local PostgreSQL server.");
        adminConnection = builder.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin).ExecuteNonQueryAsync();
            created = true;
        }
        builder.Database = databaseName;
        testConnection = builder.ConnectionString;
        var repo = FindRepoRoot();
        var docs = Environment.GetEnvironmentVariable("FIRE3D_TEST_DOCS") ?? Path.Combine(repo, "..", "Docs");
                var options = new DbContextOptionsBuilder<Fire3DDbContext>()
            .UseNpgsql(testConnection)
            .Options;
        using (var db = new Fire3DDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }
        // Raw SQL auth recovery tables are not part of the EF model.
        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "002_password_reset_recovery.sql")));
        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "003_local_password.sql")));
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = testConnection,
                ["Jwt:Issuer"] = "Fire3D.Tests", ["Jwt:Audience"] = "Fire3D.Tests.Client",
                ["Jwt:SigningKey"] = signingKey, ["Logging:LogLevel:Default"] = "Critical"
            };
            web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            web.ConfigureServices(services =>
            {
                // Minimal-host configuration callbacks can run after Program captures configuration.
                // Replace context registrations explicitly and verify the database before provisioning.
                services.RemoveAll<Fire3DDbContext>();
                services.RemoveAll<DbContextOptions<Fire3DDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<Fire3DDbContext>>();
                services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
            });
        });
        var previousFirebaseApp = FirebaseAdmin.FirebaseApp.DefaultInstance;
        try
        {
            client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        }
        finally
        {
            // Program creates a process-global SDK instance; this fixture owns only the instance it created.
            if (previousFirebaseApp is null) ownedFirebaseApp = FirebaseAdmin.FirebaseApp.DefaultInstance;
        }
        using var scope = factory.Services.CreateScope();
        Assert.Equal(databaseName, scope.ServiceProvider.GetRequiredService<Fire3DDbContext>().Database.GetDbConnection().Database);
        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new BootstrapAdminCommand("admin@example.test", password), default);
        Assert.True(result.IsSuccess, result.Error?.Code);
        adminId = result.Value!.Id;
    }

    [PostgresFact]
    public async Task Login_uses_normalized_email_hashes_tokens_and_records_audit()
    {
        var tokens = await LoginAsync("  ADMIN@EXAMPLE.TEST  ");
        Assert.Equal(UserRole.PlatformAdmin, tokens.User.Role);
        Assert.Null(tokens.User.OrganizationId);
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(64L, Convert.ToInt64(await ScalarAsync("SELECT length(token_hash) FROM auth_refresh_tokens LIMIT 1")));
        Assert.NotEqual(tokens.RefreshToken, await ScalarAsync("SELECT token_hash FROM auth_refresh_tokens LIMIT 1"));
        Assert.NotEqual(password, await ScalarAsync("SELECT password_hash FROM users LIMIT 1"));
        Assert.NotNull(await ScalarAsync("SELECT last_login_at FROM users LIMIT 1"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM audit_logs WHERE action='Login'"));
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [PostgresFact]
    public async Task Provisioning_requires_admin_and_enforces_role_organization_rules()
    {
        var request = new CreateAccountRequest("trainee@example.test", password, "Trainee", UserRole.Trainee, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/accounts", request, Json)).StatusCode);
        var admin = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/accounts",
            request with { Role = UserRole.OrganizationUser }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/accounts",
            request with { OrganizationId = Guid.NewGuid() }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/accounts",
            request with { Password = "short" }, Json)).StatusCode);
        var createdUser = await client.PostAsJsonAsync("/api/accounts", request, Json);
        Assert.Equal(HttpStatusCode.Created, createdUser.StatusCode);
        Assert.DoesNotContain("password", await createdUser.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/accounts", request, Json)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        var trainee = await LoginAsync("trainee@example.test");
        client.DefaultRequestHeaders.Authorization = new("Bearer", trainee.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/accounts",
            request with { Email = "escalate@example.test", Role = UserRole.PlatformAdmin }, Json)).StatusCode);
    }

    [PostgresFact]
    public async Task Rotation_replay_revokes_family_but_not_another_login()
    {
        var first = await LoginAsync();
        var firstRefreshExpiry = (DateTime)(await ScalarAsync("SELECT expires_at FROM auth_refresh_tokens LIMIT 1"))!;
        var other = await LoginAsync();
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        Assert.NotEqual(first.RefreshToken, rotated.RefreshToken);
        //Assert.Equal(firstRefreshExpiry);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(rotated.RefreshToken))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", rotated.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", other.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgresFact]
    public async Task Concurrent_refresh_has_one_winner_and_replay_revokes_it()
    {
        var initial = await LoginAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(initial.RefreshToken))));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM auth_refresh_tokens WHERE revoked_at IS NULL"));
    }

    [PostgresFact]
    public async Task Logout_revokes_both_access_and_refresh()
    {
        var token = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken))).StatusCode);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM audit_logs WHERE action='Logout'"));
    }

    [PostgresFact]
    public async Task Disabled_user_cannot_login_refresh_or_use_access_token()
    {
        var token = await LoginAsync();
        await ExecuteAsync("UPDATE users SET is_active=false");
        var disabledLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", password));
        Assert.Equal(HttpStatusCode.Forbidden, disabledLogin.StatusCode); // Correct password, unavailable account: documented ACCOUNT_DISABLED contract.
        var disabledProblem = await disabledLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCOUNT_DISABLED", disabledProblem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [PostgresFact]
    public async Task Organization_user_is_blocked_when_organization_is_disabled()
    {
        var org = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO organizations(id,name,slug) VALUES ('{org}', 'Test organization', 'test-org')");
        var admin = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.AccessToken);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest("org@example.test", password, "Org user", UserRole.OrganizationUser, org), Json)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        var token = await LoginAsync("org@example.test");
        Assert.Equal(org, token.User.OrganizationId);
        await ExecuteAsync("UPDATE organizations SET is_active=false");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken))).StatusCode);
    }

    [PostgresFact]
    public async Task Jwt_rejects_wrong_signature_audience_and_expiry()
    {
        var original = await LoginAsync();
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(original.AccessToken);
        foreach (var variant in new[] { "signature", "audience", "expiry" })
        {
            var key = variant == "signature" ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(signingKey);
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken("Fire3D.Tests", variant == "audience" ? "other" : "Fire3D.Tests.Client",
                parsed.Claims.Where(x => x.Type is "sub" or "sid" or "role"),
                now.AddMinutes(-10), variant == "expiry" ? now.AddMinutes(-5) : now.AddMinutes(5),
                new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    [PostgresFact]
    public async Task Expired_refresh_is_rejected_and_bootstrap_cannot_be_repeated()
    {
        var token = await LoginAsync();
        await ExecuteAsync("UPDATE auth_refresh_tokens SET created_at=now()-interval '2 days', expires_at=now()-interval '1 day'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token.RefreshToken))).StatusCode);
        using var scope = factory!.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new BootstrapAdminCommand("second@example.test", password), default);
        Assert.Equal("ADMIN_EXISTS", result.Error?.Code);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM users"));
    }

    [PostgresFact]
    public async Task Bootstrap_cli_creates_first_admin_then_refuses_to_overwrite()
    {
        await ExecuteAsync($"DELETE FROM users WHERE id='{adminId}'");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--bootstrap-admin");
        start.ArgumentList.Add("true");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DefaultConnection"] = testConnection;
        start.Environment["Jwt__Issuer"] = "Fire3D.Tests";
        start.Environment["Jwt__Audience"] = "Fire3D.Tests.Client";
        start.Environment["Jwt__SigningKey"] = signingKey;
        start.Environment["BootstrapAdmin__Email"] = "cli-admin@example.test";
        start.Environment["BootstrapAdmin__Password"] = password;
        foreach (var expected in new[] { 0, 1 })
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            await Task.WhenAll(stdout, stderr);
            Assert.Equal(expected, process.ExitCode);
        }
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM users WHERE email='cli-admin@example.test' AND role='PlatformAdmin'"));
    }

    [PostgresFact]
    public async Task Missing_role_and_deleted_accounts_are_rejected()
    {
        var token = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/accounts",
            new { email = "missing-role@example.test", password, fullName = "Missing role" })).StatusCode);
        await ExecuteAsync("UPDATE users SET deleted_at=now()");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin@example.test", password))).StatusCode);
    }

    [PostgresFact]
    public async Task Swagger_documents_bearer_and_auth_requests_are_rate_limited()
    {
        var openapi = await client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("Bearer", openapi);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("invalid"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("invalid"))).StatusCode);
    }

    private async Task<LoginResponse> LoginAsync(string email = "admin@example.test")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.TryGetProperty("accessTokenExpiresAt", out _));
        Assert.False(payload.TryGetProperty("refreshTokenExpiresAt", out _));
        return payload.Deserialize<LoginResponse>(Json)!;
    }
    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(testConnection);
        await connection.OpenAsync();
        await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync();
    }
    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(testConnection);
        await connection.OpenAsync();
        return await new NpgsqlCommand(sql, connection).ExecuteScalarAsync();
    }
    private static string FindRepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "database", "001_auth_refresh_tokens.sql"))) return path.FullName;
        throw new InvalidOperationException("Cannot find BE repository root.");
    }
    public async Task DisposeAsync()
    {
        client?.Dispose();
        try { if (factory is not null) await factory.DisposeAsync(); }
        finally { ownedFirebaseApp?.Delete(); }
        if (!created) return;
        // Only the exact random database created by this fixture may be dropped.
        if (!databaseName.StartsWith("fire3d_auth_test_", StringComparison.Ordinal)) throw new InvalidOperationException();
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin).ExecuteNonQueryAsync();
    }
}

