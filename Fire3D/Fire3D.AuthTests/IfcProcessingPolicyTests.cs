using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Infrastructure.Ifc;
using Xunit;

namespace Fire3D.AuthTests;

// #219 team decision (24/11/2026): the daily IFC processing limit is a versioned policy PlatformAdmin edits (default and per Organization);
// IfcProcessing:DailyLimitPerOrganization only applies while no policy exists.
public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Ifc_processing_policy_set_by_platform_admin_overrides_the_configured_limit()
    {
        var building = await SeedIfcBuilding();
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM buildings WHERE id='{building}'"))!;
        var revisions = new[] { await SeedDistinctIfcRevisionIn(building, "P1"), await SeedDistinctIfcRevisionIn(building, "P2"), await SeedDistinctIfcRevisionIn(building, "P3") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        using var user = await RoleClient("OrganizationUser", org);

        // No policy yet: the configured starting value (20 in tests' configuration default).
        var initial = (await user.GetFromJsonAsync<JsonElement>("/api/organizations/me/ifc-processing"));
        Assert.Equal(("Configuration", 0), (initial.GetProperty("source").GetString(), initial.GetProperty("used").GetInt32()));

        // Validation and roles.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/ifc-processing/policies", new { dailyLimit = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/ifc-processing/policies", new { dailyLimit = (int?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/admin/ifc-processing/policies", new { dailyLimit = 50 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/ifc-processing/policies")).StatusCode);

        // PlatformAdmin sets the default to 1 run per day.
        var created = await client.PostAsJsonAsync("/api/admin/ifc-processing/policies", new { dailyLimit = 1, note = "Mặc định" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var usage = await user.GetFromJsonAsync<JsonElement>("/api/organizations/me/ifc-processing");
        Assert.Equal(("Default", 1, 1), (usage.GetProperty("source").GetString(), usage.GetProperty("dailyLimit").GetInt32(), usage.GetProperty("remaining").GetInt32()));

        await WithProcessingRuntime(async (api, worker) =>
        {
            await using var db = BuildingContext(api);
            var store = new ProcessingRuntimeStore(db, ProcessingConfig(worker, dailyLimit: 5));
            var first = await store.RequestAsync(adminId, revisions[0], "policy-1", default);
            Assert.True(first.IsSuccess, first.Error?.Code);
            await ExecuteAsync($"UPDATE processing_jobs SET status='Succeeded',finished_at=now() WHERE id='{first.Value}'");
            // The policy (1) wins over the configuration (5).
            var limited = await store.RequestAsync(adminId, revisions[1], "policy-2", default);
            Assert.Equal(("IFC_PROCESS_DAILY_LIMIT", 429), (limited.Error?.Code, limited.Error?.Status));
            Assert.Equal(new[] { "1" }, limited.Error!.Errors!["limit"]);
            Assert.Equal(new[] { "1" }, limited.Error.Errors["used"]);

            // An override for this Organization raises it to 3; the default stays 1 for everyone else.
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/admin/ifc-processing/policies", new { organizationId = org, dailyLimit = 3 })).StatusCode);
            var overridden = await client.GetFromJsonAsync<JsonElement>($"/api/admin/organizations/{org}/ifc-processing");
            Assert.Equal(("Organization", 3, 1, 2), (overridden.GetProperty("source").GetString(), overridden.GetProperty("dailyLimit").GetInt32(),
                overridden.GetProperty("used").GetInt32(), overridden.GetProperty("remaining").GetInt32()));
            Assert.True((await store.RequestAsync(adminId, revisions[1], "policy-2b", default)).IsSuccess);
            await ExecuteAsync($"UPDATE processing_jobs SET status='Succeeded',finished_at=now() WHERE revision_id='{revisions[1]}'");

            // Removing the override (dailyLimit null) goes back to the default: 2 used of 1.
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/admin/ifc-processing/policies", new { organizationId = org, dailyLimit = (int?)null })).StatusCode);
            Assert.Equal("IFC_PROCESS_DAILY_LIMIT", (await store.RequestAsync(adminId, revisions[2], "policy-3", default)).Error?.Code);
        });

        // History is kept: three versions, newest first, and versions are never edited.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/ifc-processing/policies");
        Assert.Equal(3, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, list.GetProperty("effectiveDefault").GetProperty("dailyLimit").GetInt32());
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>($"/api/admin/ifc-processing/policies?organizationId={org}")).GetProperty("totalCount").GetInt32());
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync("UPDATE ifc_processing_policies SET daily_limit=99"));
    }
}
