using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<HttpClient> RoleClient(string role, Guid? organization = null)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO users(id,organization_id,email,username,full_name,role,is_active,email_verified_at,password_hash,created_at,updated_at) SELECT '{id}',{(organization is null ? "NULL" : $"'{organization}'")},'{role.ToLowerInvariant()}-{id:N}@example.test','u'||left(md5('{id}'),20),'{role}','{role}',true,now(),password_hash,now(),now() FROM users WHERE id='{adminId}'");
        var http = factory!.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync($"{role.ToLowerInvariant()}-{id:N}@example.test")).AccessToken);
        return http;
    }

    [PostgresFact]
    public async Task Learn_and_library_lifecycle_replay_by_key_and_refuse_non_admin_roles()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learn/posts") { Content = JsonContent.Create(new { slug = "key-post", publish = true, version = new { kind = "Article", title = "Exit", summary = "s", blocks = new object[] { new { type = "paragraph", text = "Use the stairs." } } } }) };
        create.Headers.Add("Idempotency-Key", "key-post");
        var postId = (await (await client.SendAsync(create)).Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var etag = (await client.GetAsync($"/api/admin/learn/posts/{postId}")).Headers.ETag!.Tag;
        HttpRequestMessage Hide(string? key, string ifMatch)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/learn/posts/{postId}/hide");
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            return request;
        }
        var first = await client.SendAsync(Hide("hide-1", etag));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // The same key and input replays the first response although the ETag has moved.
        var replay = await client.SendAsync(Hide("hide-1", etag));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{postId}' AND new_values->>'operation'='Hide'"));
        // Another input under the key is a conflict; without a key the stale ETag is still 412.
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Hide("hide-1", "\"999\""))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(Hide(null, etag))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Hide("bad key", etag))).StatusCode);

        var item = new HttpRequestMessage(HttpMethod.Post, "/api/admin/library/items") { Content = JsonContent.Create(new { kind = "Equipment", code = "key-ext" }) };
        item.Headers.Add("Idempotency-Key", "key-item");
        var itemId = (await (await client.SendAsync(item)).Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var itemTag = (await client.GetAsync($"/api/admin/library/items/{itemId}")).Headers.ETag!.Tag;
        HttpRequestMessage Patch(bool active) { var r = new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/library/items/{itemId}") { Content = JsonContent.Create(new { isActive = active }) }; r.Headers.TryAddWithoutValidation("If-Match", itemTag); r.Headers.Add("Idempotency-Key", "patch-1"); return r; }
        var patched = await client.SendAsync(Patch(false));
        Assert.True(patched.StatusCode == HttpStatusCode.OK, await patched.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Patch(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Patch(true))).StatusCode);

        // Role boundaries over HTTP: admin content routes are PlatformAdmin only; the Library catalog excludes Trainees.
        var tenant = await SeedAiTenant();
        using var org = await RoleClient("OrganizationUser", tenant.Org);
        using var trainee = await RoleClient("Trainee");
        foreach (var http in new[] { org, trainee })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/admin/learn/posts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/admin/library/items")).StatusCode);
            using var hide = Hide("role", etag);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(hide)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await org.GetAsync("/api/library/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainee.GetAsync("/api/library/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trainee.GetAsync($"/api/library/items/{itemId}")).StatusCode);
    }
}
