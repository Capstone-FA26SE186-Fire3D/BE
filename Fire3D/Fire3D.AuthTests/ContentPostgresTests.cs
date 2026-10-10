using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Content;
using Fire3D.Infrastructure.Content;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task WithContentRuntime(Func<string, Task> action)
    {
        var login = "content_runtime_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION learn_public_gate(text,uuid,uuid,jsonb),learn_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint),library_gate(text,uuid,uuid,uuid,jsonb,text,bigint),learn_rag_eligible(uuid,uuid) TO {login}");
        try { await action(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString); }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }

    private async Task<Guid> AdminFamily()
    {
        var family = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{adminId}','{family}','{Guid.NewGuid():N}{Guid.NewGuid():N}',now(),now()+interval '1 hour')");
        return family;
    }

    private async Task<Guid> SeedKnowledgeSource(string status)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO knowledge_sources(id,visibility,title,version_label,source_hash,approval_status,approved_at,created_by,created_at) VALUES('{id}','Common','QCVN 06','2022','{new string('c', 63)}{(status == "Approved" ? "1" : "2")}','{status}',{(status == "Draft" ? "NULL" : "now()")},'{adminId}',now())");
        return id;
    }

    private static LearnVersionInput LearnInput(string title, IReadOnlyList<Guid>? situations = null, IReadOnlyList<LearnSourceInput>? sources = null) =>
        new("Article", title, "Summary of " + title, null, new JsonArray(JsonNode.Parse("""{"type":"paragraph","text":"Use the nearest safe exit."}""")), situations, sources);

    [PostgresFact]
    public async Task Learn_lifecycle_public_reads_bookmarks_rag_and_invalidation()
    {
        var admin = await AdminFamily(); var trainee = await SeedTrainee();
        var approved = await SeedKnowledgeSource("Approved"); var draftSource = await SeedKnowledgeSource("Draft");
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new ContentGates(db);
            var situation = (await gates.LearnAdminAsync("CreateSituation", adminId, admin, Guid.Empty, new CreateLearnSituationRequest("fire-at-home", "Cháy tại nhà"), "situation", null, default)).Value.GetProperty("id").GetGuid();
            var content = LearnContent.Normalize(LearnInput("Thoát hiểm", [situation], [new(approved, "Primary")])).Content!;
            var created = await gates.LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug = "thoat-hiem", version = content, publish = false }, "post", null, default);
            Assert.True(created.IsSuccess, created.Error?.Code);
            var post = created.Value.GetProperty("id").GetGuid();
            var version = created.Value.GetProperty("versions")[0].GetProperty("id").GetGuid();
            Assert.Equal(created.Value.GetRawText(), (await gates.LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug = "thoat-hiem", version = content, publish = false }, "post", null, default)).Value.GetRawText());
            Assert.Equal("NOT_FOUND", (await gates.LearnPublicAsync("Post", null, null, new { slug = "thoat-hiem" }, default)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await gates.LearnPublicAsync("Bookmark", trainee.Id, trainee.Family, new { postId = post }, default)).Error?.Code);
            Assert.Equal("FORBIDDEN", (await gates.LearnAdminAsync("Get", trainee.Id, trainee.Family, post, new { }, null, null, default)).Error?.Code);

            Assert.Equal("PRECONDITION_REQUIRED", (await gates.LearnAdminAsync("PublishVersion", adminId, admin, version, new { }, null, null, default)).Error?.Code);
            Assert.Equal("PRECONDITION_FAILED", (await gates.LearnAdminAsync("PublishVersion", adminId, admin, version, new { }, null, 99, default)).Error?.Code);
            var published = await gates.LearnAdminAsync("PublishVersion", adminId, admin, version, new { }, null, 1, default);
            Assert.True(published.IsSuccess, published.Error?.Code);
            Assert.Equal("Published", published.Value.GetProperty("publicationStatus").GetString());
            var page = (await gates.LearnPublicAsync("Posts", null, null, new { situation = "fire-at-home" }, default)).Value;
            Assert.Equal(1, page.GetProperty("total").GetInt32());
            var publicPost = (await gates.LearnPublicAsync("Post", null, null, new { slug = "thoat-hiem" }, default)).Value;
            Assert.Equal("Thoát hiểm", publicPost.GetProperty("publishedVersion").GetProperty("title").GetString());
            Assert.False(publicPost.TryGetProperty("versions", out _));
            Assert.Equal("LEARN_VERSION_PUBLISHED", (await gates.LearnAdminAsync("UpdateVersion", adminId, admin, version, content, null, 2, default)).Error?.Code);
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE learn_post_versions SET title='tamper' WHERE id='{version}'"));
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM learn_post_version_sources WHERE post_version_id='{version}'"));

            Assert.True((await gates.LearnPublicAsync("Bookmark", trainee.Id, trainee.Family, new { postId = post }, default)).IsSuccess);
            var revision = published.Value.GetProperty("revision").GetInt64();
            var hidden = await gates.LearnAdminAsync("Hide", adminId, admin, post, new { }, null, revision, default);
            Assert.Equal("Hidden", hidden.Value.GetProperty("publicationStatus").GetString());
            Assert.Equal("LEARN_POST_UNAVAILABLE", (await gates.LearnPublicAsync("Post", null, null, new { slug = "thoat-hiem" }, default)).Error?.Code);
            Assert.Equal(0, (await gates.LearnPublicAsync("Posts", null, null, new { }, default)).Value.GetProperty("total").GetInt32());
            var bookmark = (await gates.LearnPublicAsync("Bookmarks", trainee.Id, trainee.Family, new { }, default)).Value.GetProperty("items")[0];
            Assert.False(bookmark.GetProperty("available").GetBoolean()); Assert.False(bookmark.TryGetProperty("title", out _));
            Assert.Equal(true, await ScalarAsync($"SELECT learn_rag_eligible('{post}','{version}')"));
            revision = hidden.Value.GetProperty("revision").GetInt64();
            var shown = await gates.LearnAdminAsync("Show", adminId, admin, post, new { }, null, revision, default);
            revision = shown.Value.GetProperty("revision").GetInt64();
            var deleted = await gates.LearnAdminAsync("Delete", adminId, admin, post, new { }, null, revision, default);
            Assert.Equal("NOT_FOUND", (await gates.LearnPublicAsync("Post", null, null, new { slug = "thoat-hiem" }, default)).Error?.Code);
            Assert.Equal(false, await ScalarAsync($"SELECT learn_rag_eligible('{post}','{version}')"));
            Assert.Equal("LEARN_POST_DELETED", (await gates.LearnAdminAsync("CreateVersion", adminId, admin, post, content, "v-deleted", null, default)).Error?.Code);
            var restored = await gates.LearnAdminAsync("Restore", adminId, admin, post, new { }, null, deleted.Value.GetProperty("revision").GetInt64(), default);
            Assert.Equal("Hidden", restored.Value.GetProperty("publicationStatus").GetString());
            Assert.Equal("LEARN_POST_UNAVAILABLE", (await gates.LearnPublicAsync("Post", null, null, new { slug = "thoat-hiem" }, default)).Error?.Code);

            // A new draft citing an unapproved source cannot publish; the earlier published version stays intact.
            var draft = await gates.LearnAdminAsync("CreateVersion", adminId, admin, post, LearnContent.Normalize(LearnInput("Bản 2", null, [new(draftSource)])).Content!, "v2", null, default);
            var v2 = draft.Value.GetProperty("id").GetGuid();
            Assert.Equal("LEARN_SOURCE_NOT_APPROVED", (await gates.LearnAdminAsync("PublishVersion", adminId, admin, v2, new { }, null, draft.Value.GetProperty("revision").GetInt64(), default)).Error?.Code);
            var fixedDraft = await gates.LearnAdminAsync("UpdateVersion", adminId, admin, v2, LearnContent.Normalize(LearnInput("Bản 2")).Content!, null, draft.Value.GetProperty("revision").GetInt64(), default);
            Assert.True(fixedDraft.IsSuccess, fixedDraft.Error?.Code);
            var republished = await gates.LearnAdminAsync("PublishVersion", adminId, admin, v2, new { }, null, fixedDraft.Value.GetProperty("revision").GetInt64(), default);
            Assert.Equal("Hidden", republished.Value.GetProperty("publicationStatus").GetString());
            Assert.Equal(v2, republished.Value.GetProperty("publishedVersionId").GetGuid());
            Assert.Equal(false, await ScalarAsync($"SELECT learn_rag_eligible('{post}','{version}')"));

            var instant = await gates.LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug = "meo-nhanh", version = LearnContent.Normalize(LearnInput("Mẹo")).Content!, publish = true }, "instant", null, default);
            Assert.Equal("Published", instant.Value.GetProperty("publicationStatus").GetString());
            Assert.Equal(7L, await ScalarAsync("SELECT count(*) FROM integration_outbox_events WHERE aggregate_type='Platform' AND event_type='PlatformCacheInvalidation' AND payload->>'scope'='learn'"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM integration_outbox_events WHERE aggregate_type='Platform' AND status<>'Pending'"));
        });
    }

    [PostgresFact]
    public async Task Library_versions_publish_immutably_and_organization_reads_published_active_items()
    {
        var admin = await AdminFamily();
        var f = await SeedReadyPackage(); var ownerFamily = await SeedPlaytestFamily(f.Owner);
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new ContentGates(db);
            var item = await gates.LibraryAsync("CreateItem", adminId, admin, Guid.Empty, new { kind = "RubricSample", code = "EXIT-BASIC" }, "item", null, default);
            Assert.True(item.IsSuccess, item.Error?.Code);
            var itemId = item.Value.GetProperty("id").GetGuid();
            Assert.Equal("FORBIDDEN", (await gates.LibraryAsync("CreateItem", f.Owner, ownerFamily, Guid.Empty, new { kind = "Equipment", code = "X" }, "x", null, default)).Error?.Code);
            var payload = JsonNode.Parse("""{"rubric":{"schema_version":"1","pass_threshold":1,"criteria":[{"id":"exit","metric":"reached_exit","mandatory":true,"weight":1,"operator":"eq","threshold":1}]}}""");
            var version = await gates.LibraryAsync("CreateVersion", adminId, admin, itemId, new { name = "Basic exit", payload, requiredCapabilities = Array.Empty<string>() }, "version", null, default);
            var versionId = version.Value.GetProperty("id").GetGuid();
            Assert.Equal(0, (await gates.LibraryAsync("List", f.Owner, ownerFamily, Guid.Empty, new { }, null, null, default)).Value.GetProperty("total").GetInt32());
            Assert.Equal("NOT_FOUND", (await gates.LibraryAsync("GetVersion", f.Owner, ownerFamily, versionId, new { }, null, null, default)).Error?.Code);
            var published = await gates.LibraryAsync("PublishVersion", adminId, admin, versionId, new { }, null, version.Value.GetProperty("revision").GetInt64(), default);
            Assert.Equal("Published", published.Value.GetProperty("status").GetString());
            Assert.Equal("LIBRARY_VERSION_PUBLISHED", (await gates.LibraryAsync("UpdateVersion", adminId, admin, versionId, new { name = "x", payload, requiredCapabilities = Array.Empty<string>() }, null, published.Value.GetProperty("revision").GetInt64(), default)).Error?.Code);
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE organization_library_versions SET name='tamper' WHERE id='{versionId}'"));
            var orgList = (await gates.LibraryAsync("List", f.Owner, ownerFamily, Guid.Empty, new { kind = "RubricSample" }, null, null, default)).Value;
            Assert.Equal(1, orgList.GetProperty("total").GetInt32());
            // Deactivation removes the item from new selections; the published version stays readable for referencing snapshots.
            var current = (await gates.LibraryAsync("Get", adminId, admin, itemId, new { admin = true }, null, null, default)).Value;
            Assert.Equal("PRECONDITION_FAILED", (await gates.LibraryAsync("UpdateItem", adminId, admin, itemId, new { isActive = false }, null, 99, default)).Error?.Code);
            Assert.True((await gates.LibraryAsync("UpdateItem", adminId, admin, itemId, new { isActive = false }, null, current.GetProperty("revision").GetInt64(), default)).IsSuccess);
            Assert.Equal(0, (await gates.LibraryAsync("List", f.Owner, ownerFamily, Guid.Empty, new { }, null, null, default)).Value.GetProperty("total").GetInt32());
            Assert.Equal("NOT_FOUND", (await gates.LibraryAsync("Get", f.Owner, ownerFamily, itemId, new { }, null, null, default)).Error?.Code);
            Assert.True((await gates.LibraryAsync("GetVersion", f.Owner, ownerFamily, versionId, new { }, null, null, default)).IsSuccess);
            Assert.Equal("FORBIDDEN", (await gates.LibraryAsync("List", f.Owner, ownerFamily, Guid.Empty, new { admin = true }, null, null, default)).Error?.Code);
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM organization_library_items WHERE id='{itemId}'"));
            Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM integration_outbox_events WHERE aggregate_type='Platform' AND payload->>'scope'='library'"));
        });
    }

    [PostgresFact]
    public async Task Learn_and_library_HTTP_contracts_validate_content_etags_and_anonymous_reads()
    {
        var adminTokens = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminTokens.AccessToken);
        var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learn/posts") { Content = JsonContent.Create(new { slug = "bad", version = new { kind = "Article", title = "<b>x</b>", summary = "s", blocks = new object[] { new { type = "paragraph", text = "ok" } } } }) };
        invalid.Headers.Add("Idempotency-Key", "http-bad");
        var rejected = await client.SendAsync(invalid);
        Assert.Equal((HttpStatusCode)422, rejected.StatusCode);
        Assert.Contains("MARKUP_NOT_ALLOWED", await rejected.Content.ReadAsStringAsync());
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learn/posts") { Content = JsonContent.Create(new { slug = "http-post", publish = true, version = new { kind = "Video", title = "Video", summary = "s", blocks = new object[] { new { type = "video", url = "https://youtu.be/dQw4w9WgXcQ" } } } }) };
        create.Headers.Add("Idempotency-Key", "http-post");
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var postId = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var detail = await client.GetAsync($"/api/admin/learn/posts/{postId}");
        var etag = detail.Headers.ETag!.Tag;
        var hide = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/learn/posts/{postId}/hide");
        Assert.Equal((HttpStatusCode)428, (await client.SendAsync(hide)).StatusCode);
        hide = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/learn/posts/{postId}/hide"); hide.Headers.TryAddWithoutValidation("If-Match", "\"999\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(hide)).StatusCode);
        Assert.Equal("YouTube", (await (await client.PostAsJsonAsync("/api/admin/learn/media/validate", new { url = "https://www.youtube.com/shorts/dQw4w9WgXcQ" })).Content.ReadFromJsonAsync<JsonObject>())!["provider"]!.GetValue<string>());
        Assert.Equal((HttpStatusCode)422, (await client.PostAsJsonAsync("/api/admin/learn/media/validate", new { url = "https://fb.watch/x/" })).StatusCode);
        using var anonymous = factory!.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/learn/posts/http-post")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/learn/bookmarks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/library/items")).StatusCode);
        hide = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/learn/posts/{postId}/hide"); hide.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(hide)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await anonymous.GetAsync("/api/learn/posts/http-post")).StatusCode);
        var item = new HttpRequestMessage(HttpMethod.Post, "/api/admin/library/items") { Content = JsonContent.Create(new { kind = "Equipment", code = "co2-ext" }) };
        item.Headers.Add("Idempotency-Key", "http-item");
        var itemResponse = await client.SendAsync(item);
        Assert.Equal(HttpStatusCode.Created, itemResponse.StatusCode);
        var itemId = (await itemResponse.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var badVersion = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/library/items/{itemId}/versions") { Content = JsonContent.Create(new { name = "CO2", payload = new { description = "CO2", enables = "fire" } }) };
        badVersion.Headers.Add("Idempotency-Key", "http-bad-version");
        Assert.Equal((HttpStatusCode)422, (await client.SendAsync(badVersion)).StatusCode);
    }
}
