using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<HttpResponseMessage> PatchOrganization(Guid id, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/organizations/{id}") { Content = JsonContent.Create(body) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request);
    }

    [PostgresFact]
    public async Task Admin_reads_and_edits_organization_profile_with_etag_counts_and_phone_conflict()
    {
        var organization = await SeedBuildingOrganization();
        var other = await SeedBuildingOrganization();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        await ExecuteAsync($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES(gen_random_uuid(),'{organization}','Active',true,'{adminId}',now(),now()),(gen_random_uuid(),'{organization}','Archived',false,'{adminId}',now(),now())");

        var read = await client.GetAsync($"/api/organizations/{organization}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var etag = read.Headers.ETag!.ToString();
        var view = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, view.GetProperty("buildingCount").GetInt32());
        Assert.Equal(etag, $"\"{view.GetProperty("profileRevision").GetInt64()}\"");

        Assert.Equal((HttpStatusCode)428, (await PatchOrganization(organization, new { address = "1 Example Street" }, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchOrganization(organization, new { address = "1 Example Street" }, "abc")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PatchOrganization(organization, new { address = "1 Example Street" }, "\"999\"")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchOrganization(organization, new { name = "" }, etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchOrganization(Guid.NewGuid(), new { name = "X" }, etag)).StatusCode);

        var updated = await PatchOrganization(organization, new { address = "1 Example Street", phoneNumber = "0901 234 567" }, etag);
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1 Example Street", body.GetProperty("address").GetString());
        Assert.Equal("0901234567", body.GetProperty("phoneNumber").GetString());
        Assert.Equal("Building Test", body.GetProperty("name").GetString());
        Assert.NotEqual(etag, updated.Headers.ETag!.ToString());
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='organizations' AND target_id='{organization}' AND user_id='{adminId}' AND new_values->>'address'='1 Example Street'"));

        // Another organization cannot take the number; its revision stays unchanged.
        var otherTag = (await client.GetAsync($"/api/organizations/{other}")).Headers.ETag!.ToString();
        var taken = await PatchOrganization(other, new { phoneNumber = "0901234567" }, otherTag);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        var problem = await taken.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ORGANIZATION_PHONE_EXISTS", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("phoneNumber", out _));
        Assert.Equal(otherTag, (await client.GetAsync($"/api/organizations/{other}")).Headers.ETag!.ToString());

        // An inactive organization stays editable by Admin; the status change moves the ETag.
        var deactivated = await client.PatchAsJsonAsync($"/api/organizations/{organization}/status", new { isActive = false });
        var inactiveTag = deactivated.Headers.ETag!.ToString();
        Assert.Equal(HttpStatusCode.OK, (await PatchOrganization(organization, new { name = "Renamed" }, inactiveTag)).StatusCode);

        // Organization users cannot use the admin route.
        var tenant = await SeedAiTenant();
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{tenant.User}'");
        using var org = factory!.CreateClient();
        org.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync($"ai-{tenant.User:N}@example.test")).AccessToken);
        using var forbidden = new HttpRequestMessage(HttpMethod.Patch, $"/api/organizations/{tenant.Org}") { Content = JsonContent.Create(new { name = "Mine" }) };
        forbidden.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.Forbidden, (await org.SendAsync(forbidden)).StatusCode);
    }

    [PostgresFact]
    public async Task Admin_lists_sort_server_side_and_accounts_expose_profile_fields_with_etag()
    {
        var first = await SeedBuildingOrganization();
        var second = await SeedBuildingOrganization();
        await ExecuteAsync($"UPDATE organizations SET name='Aaa sort' WHERE id='{first}'; UPDATE organizations SET name='Zzz sort' WHERE id='{second}'");
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        async Task<string[]> Names(string sort)
        {
            var page = await client.GetFromJsonAsync<JsonElement>($"/api/organizations?search=sort&sort={sort}");
            return page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
        }
        Assert.Equal(["Aaa sort", "Zzz sort"], await Names("name"));
        Assert.Equal(["Zzz sort", "Aaa sort"], await Names("-name"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/organizations?sort=slug")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/accounts?sort=password")).StatusCode);

        var tenant = await SeedAiTenant();
        // Avatars come only from the storage key as signed URLs; without one the account has no avatar.
        await ExecuteAsync($"UPDATE users SET username='lan.nguyen',phone_number='0912345678' WHERE id='{tenant.User}'");
        var accounts = await client.GetFromJsonAsync<JsonElement>($"/api/accounts?organizationId={tenant.Org}&sort=email");
        var account = accounts.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("lan.nguyen", account.GetProperty("username").GetString());
        Assert.Equal("0912345678", account.GetProperty("phoneNumber").GetString());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("avatarUrl").ValueKind);
        var detail = await client.GetAsync($"/api/accounts/{tenant.User}");
        var detailBody = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"\"{detailBody.GetProperty("profileRevision").GetInt64()}\"", detail.Headers.ETag!.ToString());
        Assert.Equal(JsonValueKind.Null, detailBody.GetProperty("avatarUrl").ValueKind);
    }
}
