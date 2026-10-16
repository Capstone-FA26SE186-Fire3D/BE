using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Buildings;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // AUTHZ-01: Building routes take the tenant from the database account, never from the route id, and archived
    // Buildings stay readable in the console but cannot be edited.
    [PostgresFact]
    public async Task Building_routes_hide_other_tenants_and_reject_edits_after_archive()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var owner = await CreateOrganizationAsync("bld-owner-" + Guid.NewGuid().ToString("N")[..8]);
        var stranger = await CreateOrganizationAsync("bld-other-" + Guid.NewGuid().ToString("N")[..8]);
        await CreateAccountAsync("owner-user@example.test", UserRole.OrganizationUser, owner.Id);
        await CreateAccountAsync("other-user@example.test", UserRole.OrganizationUser, stranger.Id);
        var created = await PostBuilding(client, new { organizationId = owner.Id, name = "Tower", totalFloors = 3 });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var building = (await created.Content.ReadFromJsonAsync<BuildingResponse>())!;

        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("other-user@example.test")).AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/buildings/{building.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendBuilding(client, HttpMethod.Put, building.Id, new { name = "Hijacked", totalFloors = 1 }, created.Headers.ETag!.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendBuilding(client, HttpMethod.Delete, building.Id, null, created.Headers.ETag!.Tag)).StatusCode);
        var foreignList = await client.GetAsync("/api/buildings");
        Assert.Equal(HttpStatusCode.OK, foreignList.StatusCode);
        Assert.DoesNotContain(building.Id.ToString(), await foreignList.Content.ReadAsStringAsync());
        Assert.Equal("Tower", await ScalarAsync($"SELECT name FROM buildings WHERE id='{building.Id}'"));
        Assert.Equal(true, await ScalarAsync($"SELECT is_active FROM buildings WHERE id='{building.Id}'"));

        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("owner-user@example.test")).AccessToken);
        var renamed = await SendBuilding(client, HttpMethod.Put, building.Id, new { name = "Tower A", totalFloors = 3 }, await BuildingTag(client, building.Id));
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync());
        var archive = await SendBuilding(client, HttpMethod.Delete, building.Id, null, renamed.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendBuilding(client, HttpMethod.Put, building.Id, new { name = "After archive", totalFloors = 3 }, archive.Headers.ETag!.Tag)).StatusCode);
        var archived = await client.GetAsync($"/api/buildings/{building.Id}");
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.False((await archived.Content.ReadFromJsonAsync<BuildingResponse>())!.IsActive);
        Assert.Equal("Tower A", await ScalarAsync($"SELECT name FROM buildings WHERE id='{building.Id}'"));
    }
}
