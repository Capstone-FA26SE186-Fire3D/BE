using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Fire3D.AuthTests;

// BE#237: production kept users_check, the role/organization CHECK of the hand-made Supabase baseline without FireSafetyExpert,
// so POST /api/accounts answered 500 for every expert. Databases built from migrations never had it; this test adds it back.
public sealed partial class AuthIntegrationTests
{
    private const string LegacyUsersCheck = """
        ALTER TABLE users ADD CONSTRAINT users_check CHECK ((role='OrganizationUser' AND organization_id IS NOT NULL)
          OR (role IN('PlatformAdmin','Trainee') AND organization_id IS NULL))
        """;

    private async Task<HttpResponseMessage> CreateExpertResponse() =>
        await client.PostAsJsonAsync("/api/accounts", new { email = $"expert-{Guid.NewGuid():N}@example.test", password, fullName = "Chuyên viên PCCC", role = "FireSafetyExpert" });

    private static string DropLegacyUsersCheckSql()
    {
        using var stream = typeof(Fire3D.Infrastructure.Migrations.DropLegacyUsersRoleCheck).Assembly
            .GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.DropLegacyUsersRoleCheck.sql")!;
        return new StreamReader(stream).ReadToEnd();
    }

    [PostgresFact]
    public async Task The_legacy_baseline_role_check_is_dropped_so_admins_can_create_experts()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        // Production as it was: the baseline rule refuses an expert, so the insert fails with 23514 and the API answers 500.
        await ExecuteAsync(LegacyUsersCheck);
        Assert.Equal(HttpStatusCode.InternalServerError, (await CreateExpertResponse()).StatusCode);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM users WHERE role='FireSafetyExpert'"));

        // The migration drops it; check_user_role_organization stays the one role/organization rule.
        await ExecuteAsync(DropLegacyUsersCheckSql());
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM pg_constraint WHERE conrelid='users'::regclass AND conname='users_check'"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM pg_constraint WHERE conrelid='users'::regclass AND conname='check_user_role_organization'"));
        var created = await CreateExpertResponse();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("VerifyEmail", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("firstLoginStep").GetString());
        // Running it again, or on a database whose users_check means something else, changes nothing.
        await ExecuteAsync(DropLegacyUsersCheckSql());
        await ExecuteAsync("ALTER TABLE users ADD CONSTRAINT users_check CHECK (length(email)>3)");
        await ExecuteAsync(DropLegacyUsersCheckSql());
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM pg_constraint WHERE conrelid='users'::regclass AND conname='users_check'"));
        client.DefaultRequestHeaders.Authorization = null;
    }
}
