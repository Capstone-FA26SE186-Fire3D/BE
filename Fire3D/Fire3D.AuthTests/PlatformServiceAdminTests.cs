using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Administration;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // 14/10 Docs review: PlatformAdmin manages runtime versions and Building Trials through audited gates.
    [PostgresFact]
    public async Task Admin_registers_immutable_runtime_versions_and_toggles_them()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var contracts = JsonNode.Parse("""
            {"fire.basic":{"version":"1.0.0","objectKinds":["Hazard"],"parameters":{"type":"object","properties":{"intensity":{"type":"number","minimum":0,"maximum":10}},"required":["intensity"],"additionalProperties":false}}}
            """)!.AsObject();
        var created = await client.PostAsJsonAsync("/api/admin/runtime-catalog", new CreateRuntimeCatalogRequest("7.1.0", "1", "1", ["fire.basic", "smoke.basic"], contracts));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var runtime = (await created.Content.ReadFromJsonAsync<RuntimeCatalogEntry>(Json))!;
        Assert.Equal((true, 2), (runtime.IsActive, runtime.Capabilities.Count));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/runtime-catalog", new CreateRuntimeCatalogRequest("7.1.0", "1", "1", ["fire.basic"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/runtime-catalog", new CreateRuntimeCatalogRequest("7.2", "1", "1", []))).StatusCode);
        var undeclared = JsonNode.Parse("""{"wind.basic":{"version":"1.0.0","objectKinds":["Hazard"]}}""")!.AsObject();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/runtime-catalog", new CreateRuntimeCatalogRequest("7.3.0", "1", "1", ["fire.basic"], undeclared))).StatusCode);

        var deactivated = await client.PatchAsJsonAsync($"/api/admin/runtime-catalog/{runtime.Id}", new SetRuntimeCatalogActiveRequest(false));
        Assert.False((await deactivated.Content.ReadFromJsonAsync<RuntimeCatalogEntry>(Json))!.IsActive);
        Assert.False((bool)(await ScalarAsync($"SELECT is_active FROM runtime_compatibility_catalog WHERE id='{runtime.Id}'"))!);
        var listed = (await client.GetFromJsonAsync<List<RuntimeCatalogEntry>>("/api/admin/runtime-catalog", Json))!;
        Assert.Contains(listed, x => x.Id == runtime.Id && !x.IsActive);
        Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='RuntimeCatalog' AND target_id='{runtime.Id}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/admin/runtime-catalog/{Guid.NewGuid()}", new SetRuntimeCatalogActiveRequest(true))).StatusCode);

        await CreateAccountAsync("runtime-org@example.test", Fire3D.Domain.Enums.UserRole.OrganizationUser, (await CreateOrganizationAsync("runtime-org-" + Guid.NewGuid().ToString("N")[..6])).Id);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("runtime-org@example.test")).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/runtime-catalog")).StatusCode);
    }

    [PostgresFact]
    public async Task Admin_grants_adjusts_and_cancels_a_building_trial()
    {
        var f = await SeedReadyPackage("PlaytestPackage");
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        var package = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at) VALUES('{package}','trial-{package:N}','Trial preview',100,'VND',1,'{{}}',true,'{adminId}',now(),now())");
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var url = $"/api/admin/buildings/{building}/trials";

        var created = await client.PostAsJsonAsync(url, new CreateTrialRequest(package, 7, 2));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var trial = (await created.Content.ReadFromJsonAsync<TrialEntitlementView>(Json))!;
        Assert.Equal(("Trial", 2, true), (trial.Status, trial.PlaytestUnitsGranted, trial.IsEffective));
        Assert.Equal(TimeSpan.FromDays(7), trial.EndsAt - trial.StartsAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new CreateTrialRequest(package, 7, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new CreateTrialRequest(package, 91, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/buildings/{Guid.NewGuid()}/trials", new CreateTrialRequest(package, 7, 2))).StatusCode);

        // Units cannot drop below what playtests already consumed; dates stay immutable (billing guard).
        await ExecuteAsync($"UPDATE service_entitlements SET playtest_units_used=2 WHERE id='{trial.Id}'");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"{url}/{trial.Id}", new UpdateTrialRequest(PlaytestUnits: 1))).StatusCode);
        var adjusted = await client.PatchAsJsonAsync($"{url}/{trial.Id}", new UpdateTrialRequest(PlaytestUnits: 5));
        Assert.True(adjusted.IsSuccessStatusCode, await adjusted.Content.ReadAsStringAsync());
        Assert.Equal(5, (await adjusted.Content.ReadFromJsonAsync<TrialEntitlementView>(Json))!.PlaytestUnitsGranted);
        var cancelled = await client.PatchAsJsonAsync($"{url}/{trial.Id}", new UpdateTrialRequest(Cancel: true));
        Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<TrialEntitlementView>(Json))!.Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, new CreateTrialRequest(package, 3, 1))).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<TrialEntitlementView>>(url, Json))!.Count);
        Assert.Equal(4L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='ServiceEntitlement' AND new_values->>'buildingId'='{building}'"));

        // A paid period blocks a Trial on top of it.
        var paid = await PublishedTraining();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/admin/buildings/{paid.Building}/trials", new CreateTrialRequest(package, 7, 2))).StatusCode);
    }
}
