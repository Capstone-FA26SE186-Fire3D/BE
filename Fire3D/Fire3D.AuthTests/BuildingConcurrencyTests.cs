using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Buildings;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static Task<HttpResponseMessage> PostBuilding(HttpClient http, object body, string? key = "auto")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/buildings") { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key == "auto" ? Guid.NewGuid().ToString("N") : key);
        return http.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendBuilding(HttpClient http, HttpMethod method, Guid id, object? body, string? ifMatch)
    {
        var request = new HttpRequestMessage(method, $"/api/buildings/{id}") { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return http.SendAsync(request);
    }

    private static async Task<string> BuildingTag(HttpClient http, Guid id)
    {
        var response = await http.GetAsync($"/api/buildings/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    // Issue #94: POST /api/buildings replays per actor/Idempotency-Key, PUT/DELETE need the Building ETag in If-Match.
    [PostgresFact]
    public async Task Building_create_is_idempotent_and_updates_need_a_current_etag()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var owner = await CreateOrganizationAsync("bld-etag-" + Guid.NewGuid().ToString("N")[..8]);
        var stranger = await CreateOrganizationAsync("bld-etag-x-" + Guid.NewGuid().ToString("N")[..8]);
        await CreateAccountAsync("etag-owner@example.test", UserRole.OrganizationUser, owner.Id);
        await CreateAccountAsync("etag-other@example.test", UserRole.OrganizationUser, stranger.Id);
        using var org = factory!.CreateClient();
        org.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("etag-owner@example.test")).AccessToken);
        var body = new { name = "Tower", totalFloors = 3, location = new { address = "1 Example Street", city = "Hanoi" } };

        var missing = await PostBuilding(org, body, key: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_REQUIRED", await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await PostBuilding(org, body, key: "has space")).StatusCode);

        var key = Guid.NewGuid().ToString("N");
        var first = await PostBuilding(org, body, key);
        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        var building = (await first.Content.ReadFromJsonAsync<BuildingResponse>())!;
        var replay = await PostBuilding(org, body, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(building.Id, (await replay.Content.ReadFromJsonAsync<BuildingResponse>())!.Id);
        Assert.Equal(first.Headers.ETag!.Tag, replay.Headers.ETag!.Tag);
        var conflict = await PostBuilding(org, new { name = "Other tower", totalFloors = 3 }, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_CONFLICT", await conflict.Content.ReadAsStringAsync());
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM buildings WHERE organization_id='{owner.Id}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{building.Id}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM application_command_receipts WHERE operation_name='Building.Create' AND idempotency_key='{key}'"));

        // Keys are per actor: another tenant's user reusing the key creates its own Building.
        using var other = factory!.CreateClient();
        other.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("etag-other@example.test")).AccessToken);
        var foreign = await PostBuilding(other, body, key);
        Assert.Equal(HttpStatusCode.Created, foreign.StatusCode);
        Assert.NotEqual(building.Id, (await foreign.Content.ReadFromJsonAsync<BuildingResponse>())!.Id);

        var tag = await BuildingTag(org, building.Id);
        Assert.Equal(first.Headers.ETag!.Tag, tag);
        var update = new { name = "Tower A", totalFloors = 4 };
        var noTag = await SendBuilding(org, HttpMethod.Put, building.Id, update, null);
        Assert.Equal((HttpStatusCode)428, noTag.StatusCode);
        Assert.Contains("PRECONDITION_REQUIRED", await noTag.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendBuilding(org, HttpMethod.Put, building.Id, update, "W/abc")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendBuilding(other, HttpMethod.Put, building.Id, update, tag)).StatusCode);

        var updated = await SendBuilding(org, HttpMethod.Put, building.Id, update, tag);
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        var newTag = updated.Headers.ETag!.Tag;
        Assert.NotEqual(tag, newTag);
        Assert.Equal(newTag, await BuildingTag(org, building.Id));

        // A second writer still holding the old ETag gets 412 with the stored Building and nothing is written.
        var stale = await SendBuilding(org, HttpMethod.Put, building.Id, new { name = "Lost update", totalFloors = 1 }, tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(newTag, stale.Headers.ETag!.Tag);
        using (var problem = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()))
        {
            Assert.Equal("PRECONDITION_FAILED", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal("Tower A", problem.RootElement.GetProperty("current").GetProperty("name").GetString());
        }
        Assert.Equal("Tower A", await ScalarAsync($"SELECT name FROM buildings WHERE id='{building.Id}'"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendBuilding(org, HttpMethod.Delete, building.Id, null, tag)).StatusCode);
        Assert.Equal(true, await ScalarAsync($"SELECT is_active FROM buildings WHERE id='{building.Id}'"));

        Assert.Equal((HttpStatusCode)428, (await SendBuilding(org, HttpMethod.Delete, building.Id, null, null)).StatusCode);
        var archived = await SendBuilding(org, HttpMethod.Delete, building.Id, null, newTag);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var archivedTag = archived.Headers.ETag!.Tag;
        Assert.NotEqual(newTag, archivedTag);
        Assert.False((await archived.Content.ReadFromJsonAsync<BuildingSummaryResponse>())!.IsActive);
        // Retrying the DELETE with the ETag it was sent with succeeds without another audit row.
        var retried = await SendBuilding(org, HttpMethod.Delete, building.Id, null, newTag);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(archivedTag, retried.Headers.ETag!.Tag);
        Assert.Equal(3L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{building.Id}'"));
    }
}
