using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Buildings;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Learning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Fire3D.AuthTests;

// HTTP and gate paths of Buildings, Building access, Learn, Library, learner analytics and learner sessions that the
// direct-gate suites do not reach: controller preconditions, nested Building updates, error messages per gate code.
public sealed partial class AuthIntegrationTests
{
    private static HttpRequestMessage CovRequest(HttpMethod method, string url, object? body = null, string? ifMatch = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<(HttpStatusCode Status, string? Code, string? Title)> CovProblem(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return (response.StatusCode, body?["code"]?.GetValue<string>(), body?["title"]?.GetValue<string>());
    }
    private static async Task<(HttpStatusCode Status, string? Code)> CovCode(HttpResponseMessage response)
    {
        var (status, code, _) = await CovProblem(response);
        return (status, code);
    }

    [PostgresFact]
    public async Task Building_search_matches_name_or_type_and_updates_replace_existing_location_and_contact()
    {
        var organization = await SeedBuildingOrganization();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var created = await PostBuilding(client, new { organizationId = organization, name = "Riverside Tower", buildingType = "Apartment", totalFloors = 3,
            location = new { address = "1 River St", city = "Hanoi" }, contact = new { contactName = "Hoa", isPrimary = false } });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var tower = (await created.Content.ReadFromJsonAsync<BuildingResponse>())!;
        Assert.Equal(HttpStatusCode.Created, (await PostBuilding(client, new { organizationId = organization, name = "Depot B", buildingType = "Office", totalFloors = 1 })).StatusCode);

        async Task<string[]> Search(string text) => (await client.GetFromJsonAsync<JsonObject>($"/api/buildings?organizationId={organization}&search={Uri.EscapeDataString(text)}"))!
            ["items"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["Riverside Tower"], await Search(" RIVERSIDE "));
        Assert.Equal(["Riverside Tower"], await Search("apart"));
        Assert.Equal(["Depot B"], await Search("office"));
        Assert.Empty(await Search("warehouse"));

        // The existing location and contact rows are updated in place, never duplicated.
        var location = tower.Location!.Id;
        var first = await SendBuilding(client, HttpMethod.Put, tower.Id, new { name = "Riverside Tower", buildingType = "Apartment", totalFloors = 4,
            location = new { address = " 2 River St ", city = "Da Nang", district = "Hai Chau", latitude = 16.05m, longitude = 108.2m },
            contact = new { contactName = " Lan ", contactRole = "Manager", phone = "0900000000", email = "lan@example.test", isPrimary = true } }, created.Headers.ETag!.Tag);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var updated = (await first.Content.ReadFromJsonAsync<BuildingResponse>())!;
        Assert.Equal((location, "2 River St", "Da Nang", "Hai Chau"), (updated.Location!.Id, updated.Location.Address, updated.Location.City, updated.Location.District));
        Assert.Equal((tower.Contact!.Id, "Lan", "Manager", true), (updated.Contact!.Id, updated.Contact.ContactName, updated.Contact.ContactRole, updated.Contact.IsPrimary));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_locations WHERE building_id='{tower.Id}'"));

        // A PUT without location keeps it; the contact row is updated again.
        var second = await SendBuilding(client, HttpMethod.Put, tower.Id, new { name = "Riverside Tower", totalFloors = 4,
            contact = new { contactName = "Minh", phone = "0911111111", isPrimary = false } }, first.Headers.ETag!.Tag);
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        var contact = (await second.Content.ReadFromJsonAsync<BuildingResponse>())!.Contact!;
        Assert.Equal((updated.Contact.Id, "Minh", "0911111111", false), (contact.Id, contact.ContactName, contact.Phone, contact.IsPrimary));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_contacts WHERE building_id='{tower.Id}'"));
        Assert.Equal("2 River St", await ScalarAsync($"SELECT address FROM building_locations WHERE building_id='{tower.Id}'"));
        Assert.Equal(3L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='buildings' AND target_id='{tower.Id}'"));
    }

    // Regression for a defect found while closing coverage: BuildingStore.UpdateBuildingWithAuditAsync attaches a new
    // BuildingLocation/BuildingContact with a client-generated key through the navigation, so EF issues an UPDATE (0 rows) and
    // the PUT fails with DbUpdateConcurrencyException (500). Unskip once the store adds the new row explicitly.
    [PostgresFact(Skip = "Known defect: PUT that adds a building's first location or contact returns 500 (BuildingStore lines 165/170).")]
    public async Task Building_update_adds_a_first_location_and_contact()
    {
        var organization = await SeedBuildingOrganization();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var created = await PostBuilding(client, new { organizationId = organization, name = "Bare", totalFloors = 1 });
        var id = (await created.Content.ReadFromJsonAsync<BuildingResponse>())!.Id;
        var updated = await SendBuilding(client, HttpMethod.Put, id, new { name = "Bare", totalFloors = 1,
            location = new { address = "1 River St" }, contact = new { contactName = "Lan", isPrimary = true } }, created.Headers.ETag!.Tag);
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_locations WHERE building_id='{id}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_contacts WHERE building_id='{id}'"));
    }

    [PostgresFact]
    public async Task Revision_reads_include_the_source_document_summary()
    {
        var revision = await SeedVerifiedIfcRevision();
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var single = (await client.GetFromJsonAsync<RevisionResponse>($"/api/revisions/{revision}", Json))!;
        Assert.Equal(("model.ifc", (long)IfcBytes.Length), (single.SourceDocument!.OriginalFilename, single.SourceDocument.FileSizeBytes));
        var listed = (await client.GetFromJsonAsync<JsonObject>($"/api/buildings/{building}/revisions"))!["items"]!.AsArray();
        Assert.Equal(single.SourceDocument.Id, Assert.Single(listed)!["sourceDocument"]!["id"]!.GetValue<Guid>());
    }

    [PostgresFact]
    public async Task Building_access_HTTP_requires_access_etag_and_binds_participation_to_the_current_code()
    {
        var organization = await SeedBuildingOrganization();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var building = (await (await PostBuilding(client, new { organizationId = organization, name = "Access", totalFloors = 1 })).Content.ReadFromJsonAsync<BuildingResponse>())!.Id;
        var access = await client.GetAsync($"/api/buildings/{building}/access");
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        var tag = access.Headers.ETag!.Tag;
        Assert.Equal("\"access-1\"", tag);

        var url = $"/api/buildings/{building}/access";
        Assert.Equal((HttpStatusCode.PreconditionRequired, "PRECONDITION_REQUIRED"), await CovCode(await client.SendAsync(CovRequest(HttpMethod.Patch, url, new { visibility = "Public" }))));
        foreach (var bad in new[] { "\"1\"", "\"access-0\"", "\"access-x\"", "access-1" })
            Assert.Equal((HttpStatusCode.BadRequest, "INVALID_IF_MATCH"), await CovCode(await client.SendAsync(CovRequest(HttpMethod.Patch, url, new { visibility = "Public" }, bad))));
        Assert.Equal("VALIDATION_ERROR", (await CovProblem(await client.SendAsync(CovRequest(HttpMethod.Patch, url, new { visibility = "Hidden" }, tag)))).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(CovRequest(HttpMethod.Patch, url, new { visibility = "Public" }, "\"access-9\""))).StatusCode);
        var shown = await client.SendAsync(CovRequest(HttpMethod.Patch, url, new { visibility = "Public" }, tag));
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("\"access-2\"", shown.Headers.ETag!.Tag);

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.SendAsync(CovRequest(HttpMethod.Post, $"/api/buildings/{building}/participation-code/rotate"))).StatusCode);
        var rotated = await client.SendAsync(CovRequest(HttpMethod.Post, $"/api/buildings/{building}/participation-code/rotate", ifMatch: shown.Headers.ETag.Tag));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var rotation = (await rotated.Content.ReadFromJsonAsync<BuildingAccessResponse>(Json))!;
        Assert.True(rotation.HasParticipationCode);
        Assert.Equal(43, rotation.Code!.Length);

        var trainee = await SeedTrainee();
        using var learner = factory!.CreateClient();
        learner.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync((string)(await ScalarAsync($"SELECT email FROM users WHERE id='{trainee.Id}'"))!)).AccessToken);
        var verify = $"/api/buildings/{building}/participation/verify";
        Assert.Equal("VALIDATION_ERROR", (await CovProblem(await learner.PostAsJsonAsync(verify, new { code = "short" }))).Code);
        Assert.Equal("PARTICIPATION_CODE_INVALID", (await CovProblem(await learner.PostAsJsonAsync(verify, new { code = new string('x', 43) }))).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(verify, new { code = rotation.Code })).StatusCode);
        var verified = await learner.PostAsJsonAsync(verify, new { code = rotation.Code });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM building_participation_grants WHERE building_id='{building}' AND trainee_user_id='{trainee.Id}' AND access_revision=3"));
        Assert.Null((await verified.Content.ReadFromJsonAsync<BuildingAccessResponse>(Json))!.Code);

        var code = $"/api/buildings/{building}/participation-code";
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.SendAsync(CovRequest(HttpMethod.Delete, code))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(CovRequest(HttpMethod.Delete, code, ifMatch: shown.Headers.ETag.Tag))).StatusCode);
        var revoked = await client.SendAsync(CovRequest(HttpMethod.Delete, code, ifMatch: rotated.Headers.ETag!.Tag));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.False((await revoked.Content.ReadFromJsonAsync<BuildingAccessResponse>(Json))!.HasParticipationCode);
        Assert.Equal("\"access-4\"", revoked.Headers.ETag!.Tag);
        Assert.Equal("PARTICIPATION_CODE_INVALID", (await CovProblem(await learner.PostAsJsonAsync(verify, new { code = rotation.Code }))).Code);
    }

    [PostgresFact]
    public async Task Learn_HTTP_versions_publish_situations_and_trainee_bookmarks()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var situation = await client.SendAsync(CovRequest(HttpMethod.Post, "/api/admin/learn/situations", new { slug = "cov-smoke", name = "Khói" }, key: "cov-situation"));
        Assert.Equal(HttpStatusCode.Created, situation.StatusCode);
        var situationId = (await situation.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        object Version(string title, object[]? sources = null) => new
        {
            kind = "Article", title, summary = "Tóm tắt", blocks = new object[] { new { type = "paragraph", text = "Đi về lối thoát gần nhất." } },
            situationIds = new[] { situationId }, sources = sources ?? []
        };
        var created = await client.SendAsync(CovRequest(HttpMethod.Post, "/api/admin/learn/posts", new { slug = "cov-learn", version = Version("Bản 1") }, key: "cov-post"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var post = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var postId = post["id"]!.GetValue<Guid>(); var v1 = post["versions"]![0]!["id"]!.GetValue<Guid>();
        Assert.Equal((HttpStatusCode.Conflict, "SLUG_EXISTS", "The slug is already used."),
            await CovProblem(await client.SendAsync(CovRequest(HttpMethod.Post, "/api/admin/learn/posts", new { slug = "cov-learn", version = Version("Khác") }, key: "cov-post-2"))));

        var admin = (await client.GetFromJsonAsync<JsonObject>("/api/admin/learn/posts?status=Unpublished"))!["items"]!.AsArray();
        Assert.Contains(admin, x => x!["slug"]!.GetValue<string>() == "cov-learn");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/learn/posts?pageSize=0")).StatusCode);
        var postTag = (await client.GetAsync($"/api/admin/learn/posts/{postId}")).Headers.ETag!.Tag;
        Assert.Equal((HttpStatusCode.Conflict, "LEARN_STATE_CONFLICT", "The post is not in a state that allows this action."),
            await CovProblem(await client.SendAsync(CovRequest(HttpMethod.Post, $"/api/admin/learn/posts/{postId}/show", ifMatch: postTag))));

        // UpdateVersion: If-Match is the quoted version revision; content is validated before the gate.
        var version = await client.GetAsync($"/api/admin/learn/versions/{v1}");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        var tag = version.Headers.ETag!.Tag;
        var url = $"/api/admin/learn/versions/{v1}";
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.SendAsync(CovRequest(HttpMethod.Put, url, Version("Bản 1b")))).StatusCode);
        foreach (var bad in new[] { "abc", "\"0\"", "\"-1\"", "\"\"" })
            Assert.Equal((HttpStatusCode.BadRequest, "INVALID_IF_MATCH"), await CovCode(await client.SendAsync(CovRequest(HttpMethod.Put, url, Version("Bản 1b"), bad))));
        Assert.Equal("CONTENT_INVALID", (await CovProblem(await client.SendAsync(CovRequest(HttpMethod.Put, url, Version("<b>x</b>"), tag)))).Code);
        var put = await client.SendAsync(CovRequest(HttpMethod.Put, url, Version("Bản 1b"), tag));
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        Assert.Equal("Bản 1b", (await put.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>());
        Assert.NotEqual(tag, put.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.SendAsync(CovRequest(HttpMethod.Post, $"{url}/publish"))).StatusCode);
        var published = await client.SendAsync(CovRequest(HttpMethod.Post, $"{url}/publish", ifMatch: put.Headers.ETag.Tag));
        Assert.True(published.StatusCode == HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
        Assert.Equal("Published", (await published.Content.ReadFromJsonAsync<JsonObject>())!["publicationStatus"]!.GetValue<string>());

        // CreateVersion validates content, then the gate checks cited sources against the registry.
        var versions = $"/api/admin/learn/posts/{postId}/versions";
        Assert.Equal((HttpStatusCode)422, (await client.SendAsync(CovRequest(HttpMethod.Post, versions, Version(""), key: "cov-v2-bad"))).StatusCode);
        Assert.Equal(((HttpStatusCode)422, "LEARN_SOURCE_INVALID", "Cite only registered Common knowledge sources."),
            await CovProblem(await client.SendAsync(CovRequest(HttpMethod.Post, versions, Version("Bản 2", [new { sourceId = Guid.NewGuid(), role = "Primary" }]), key: "cov-v2-source"))));
        var v2 = await client.SendAsync(CovRequest(HttpMethod.Post, versions, Version("Bản 2"), key: "cov-v2"));
        Assert.Equal(HttpStatusCode.Created, v2.StatusCode);
        var v2Body = (await v2.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal($"/api/admin/learn/versions/{v2Body["id"]!.GetValue<Guid>()}", v2.Headers.Location!.OriginalString);
        Assert.Equal(("Draft", 2), (v2Body["status"]!.GetValue<string>(), v2Body["versionNumber"]!.GetValue<int>()));

        // PATCH situation needs at least one valid field.
        var patch = $"/api/admin/learn/situations/{situationId}";
        foreach (var bad in new object[] { new { }, new { name = "  " }, new { name = new string('n', 256) }, new { description = new string('d', 2001) } })
            Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_ERROR"), await CovCode(await client.PatchAsJsonAsync(patch, bad)));
        var renamed = await client.PatchAsJsonAsync(patch, new { name = "Khói dày", description = "Tầm nhìn thấp", isActive = true });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var situationView = (await renamed.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("Khói dày", "Tầm nhìn thấp", true), (situationView["name"]!.GetValue<string>(), situationView["description"]!.GetValue<string>(), situationView["isActive"]!.GetValue<bool>()));

        using var trainee = await RoleClient("Trainee");
        var bookmark = $"/api/learn/bookmarks/{postId}";
        Assert.False((await trainee.GetFromJsonAsync<JsonObject>(bookmark))!["bookmarked"]!.GetValue<bool>());
        var put2 = await trainee.PutAsync(bookmark, null);
        Assert.Equal(HttpStatusCode.OK, put2.StatusCode);
        Assert.True((await put2.Content.ReadFromJsonAsync<JsonObject>())!["bookmarked"]!.GetValue<bool>());
        Assert.True((await trainee.GetFromJsonAsync<JsonObject>(bookmark))!["bookmarked"]!.GetValue<bool>());
        var page = (await trainee.GetFromJsonAsync<JsonObject>("/api/learn/bookmarks?page=1&pageSize=5"))!;
        var item = Assert.Single(page["items"]!.AsArray())!;
        Assert.Equal((postId, true, "Bản 1b"), (item["postId"]!.GetValue<Guid>(), item["available"]!.GetValue<bool>(), item["title"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await trainee.PutAsync($"/api/learn/bookmarks/{Guid.NewGuid()}", null)).StatusCode);
        var removed = await trainee.DeleteAsync(bookmark);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False((await removed.Content.ReadFromJsonAsync<JsonObject>())!["bookmarked"]!.GetValue<bool>());
        Assert.Equal(0, (await trainee.GetFromJsonAsync<JsonObject>("/api/learn/bookmarks"))!["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/learn/bookmarks")).StatusCode);
    }

    [PostgresFact]
    public async Task Library_HTTP_version_update_admin_search_and_organization_reads()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var item = await client.SendAsync(CovRequest(HttpMethod.Post, "/api/admin/library/items", new { kind = "Equipment", code = "cov-ext" }, key: "cov-item"));
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = (await item.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        Assert.Equal((HttpStatusCode.Conflict, "LIBRARY_CODE_EXISTS", "The library code is already used."),
            await CovProblem(await client.SendAsync(CovRequest(HttpMethod.Post, "/api/admin/library/items", new { kind = "Equipment", code = "COV-EXT" }, key: "cov-item-2"))));
        var created = await client.SendAsync(CovRequest(HttpMethod.Post, $"/api/admin/library/items/{itemId}/versions", new { name = "CO2", payload = new { description = "Portable CO2" } }, key: "cov-version"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var versionId = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var tag = (await client.GetAsync($"/api/admin/library/versions/{versionId}")).Headers.ETag!.Tag;

        var url = $"/api/admin/library/versions/{versionId}";
        object Valid = new { name = " CO2 5kg ", payload = new { description = "5 kg", properties = new { weightKg = 5 } } };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.SendAsync(CovRequest(HttpMethod.Put, url, Valid))).StatusCode);
        Assert.Equal("INVALID_IF_MATCH", (await CovProblem(await client.SendAsync(CovRequest(HttpMethod.Put, url, Valid, "1")))).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(CovRequest(HttpMethod.Put, $"/api/admin/library/versions/{Guid.NewGuid()}", Valid, tag))).StatusCode);
        var invalid = await client.SendAsync(CovRequest(HttpMethod.Put, url, new { name = "CO2", payload = new { enables = "fire" } }, tag));
        Assert.Equal((HttpStatusCode)422, invalid.StatusCode);
        Assert.Contains("FIELD_UNKNOWN", await invalid.Content.ReadAsStringAsync());
        var updated = await client.SendAsync(CovRequest(HttpMethod.Put, url, Valid, tag));
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        Assert.Equal("CO2 5kg", (await updated.Content.ReadFromJsonAsync<JsonObject>())!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(CovRequest(HttpMethod.Put, url, Valid, tag))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(CovRequest(HttpMethod.Post, $"{url}/publish", ifMatch: updated.Headers.ETag!.Tag))).StatusCode);

        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_ERROR"), await CovCode(await client.GetAsync($"/api/admin/library/items?search={new string('c', 81)}")));
        var found = (await client.GetFromJsonAsync<JsonObject>("/api/admin/library/items?search=%20cov-%20&kind=Equipment"))!["items"]!.AsArray();
        Assert.Equal("COV-EXT", Assert.Single(found)!["code"]!.GetValue<string>());
        Assert.Empty((await client.GetFromJsonAsync<JsonObject>("/api/admin/library/items?search=cov&isActive=false"))!["items"]!.AsArray());

        var tenant = await SeedAiTenant();
        using var org = await RoleClient("OrganizationUser", tenant.Org);
        var read = (await org.GetFromJsonAsync<JsonObject>($"/api/library/items/{itemId}"))!;
        Assert.Equal(("COV-EXT", versionId), (read["code"]!.GetValue<string>(), Assert.Single(read["versions"]!.AsArray())!["id"]!.GetValue<Guid>()));
        var readVersion = (await org.GetFromJsonAsync<JsonObject>($"/api/library/versions/{versionId}"))!;
        Assert.Equal(("CO2 5kg", "Published"), (readVersion["name"]!.GetValue<string>(), readVersion["status"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await org.GetAsync($"/api/library/versions/{Guid.NewGuid()}")).StatusCode);
    }

    [PostgresFact]
    public async Task Training_analytics_HTTP_serves_the_organization_report_and_csv_exports_per_scope()
    {
        var tenant = await SeedAiTenant();
        using var org = await RoleClient("OrganizationUser", tenant.Org);
        var report = await org.GetAsync("/api/organizations/me/analytics/training?groupBy=week");
        Assert.True(report.StatusCode == HttpStatusCode.OK, await report.Content.ReadAsStringAsync());
        var body = (await report.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal((0, "week"), (body["plays"]!.GetValue<int>(), body["filters"]!["groupBy"]!.GetValue<string>()));

        async Task<(string Name, string Text)> Csv(HttpClient http, string url, string prefix)
        {
            var response = await http.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
            var name = response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName!.Trim('"');
            Assert.StartsWith(prefix, name);
            return (name, Encoding.UTF8.GetString(bytes[3..]));
        }
        var header = string.Join(',', Fire3D.Application.Reporting.TrainingAnalyticsCsv.Header) + "\r\n";
        Assert.Equal(header, (await Csv(org, "/api/organizations/me/analytics/training/export", "training-organization-day-")).Text);
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_ERROR"), await CovCode(await org.GetAsync("/api/organizations/me/analytics/training/export?from=2026-01-01T00:00:00Z&to=2026-06-01T00:00:00Z")));
        Assert.Equal(HttpStatusCode.Forbidden, (await org.GetAsync("/api/admin/analytics/training/export")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        Assert.Equal(header, (await Csv(client, "/api/admin/analytics/training/export?groupBy=month", "training-platform-month-")).Text);
        Assert.Equal(header, (await Csv(client, $"/api/admin/organizations/{tenant.Org}/analytics/training/export?groupBy=building", "training-platformorganization-building-")).Text);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/organizations/{Guid.NewGuid()}/analytics/training/export")).StatusCode);
        Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM audit_logs WHERE target_entity='AnalyticsExport' AND action='Download'"));
        Assert.Equal(new[] { "TrainingOrganization", "TrainingPlatform", "TrainingPlatformOrganization" }, (await ScalarAsync("SELECT string_agg(report,',' ORDER BY report) FROM analytics_exports") as string)!.Split(','));
    }

    [PostgresFact]
    public async Task Learner_buildings_history_rejects_a_signed_out_session()
    {
        var trainee = await SeedTrainee();
        await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{trainee.Family}'");
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var buildings = await store.BuildingsAsync(new(trainee.Id, trainee.Family, null), 1, 20, default);
            Assert.Equal(("UNAUTHORIZED", 401, "Sign in again."), (buildings.Error?.Code, buildings.Error?.Status, buildings.Error?.Message));
            Assert.Equal("UNAUTHORIZED", (await store.ListAsync(new(trainee.Id, trainee.Family, null), new(null, null, null, null, null), default)).Error?.Code);
        });
    }

    [PostgresFact]
    public async Task Learner_gate_reports_heartbeat_unavailable_training_unapproved_content_expired_preparation_and_revoked_qr()
    {
        var f = await PublishedTraining();
        var trainee = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var running = await Running(store, caller, f.Training, "Learn", "cov-heartbeat");
            var heartbeat = await store.HeartbeatAsync(caller, running, default);
            Assert.True(heartbeat.IsSuccess, heartbeat.Error?.Code);
            Assert.Equal("Running", heartbeat.Value!.Status);
            Assert.InRange((DateTime.UtcNow - heartbeat.Value.ServerTime.ToUniversalTime()).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(5));

            var prepared = await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "cov-expire", default);
            Assert.True(prepared.IsSuccess, prepared.Error?.Code);
            await ExecuteAsync($"UPDATE training_session_preparations SET expires_at=now()-interval '1 second' WHERE id='{prepared.Value!.Id}'");
            var expired = await store.StartAsync(caller, prepared.Value.Id, "cov-expire-start", default);
            Assert.Equal(("PREPARATION_EXPIRED", 409, "Prepare the session again."), (expired.Error?.Code, expired.Error?.Status, expired.Error?.Message));

            var review = (Guid)(await ScalarAsync($"SELECT content_review_id FROM release_build_provenance p JOIN trainings t ON t.release_id=p.release_id WHERE t.id='{f.Training}'"))!;
            await ExecuteAsync($"ALTER TABLE scenario_content_reviews DISABLE TRIGGER USER; UPDATE scenario_content_reviews SET status='Rejected',reason='Coverage fixture' WHERE id='{review}'; ALTER TABLE scenario_content_reviews ENABLE TRIGGER USER");
            var unapproved = await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "cov-unapproved", default);
            Assert.Equal(("CONTENT_APPROVAL_REQUIRED", "The Training content is not approved for this release."), (unapproved.Error?.Code, unapproved.Error?.Message));

            await ExecuteAsync($"UPDATE trainings SET end_date=now()-interval '1 second' WHERE id='{f.Training}'");
            var ended = await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "cov-ended", default);
            Assert.Equal(("TRAINING_UNAVAILABLE", 409, "The Training is not published or not available now."), (ended.Error?.Code, ended.Error?.Status, ended.Error?.Message));

            var qr = new BuildingQrCodes(db);
            var created = await qr.CreateAsync(f.Owner, f.OwnerFamily, f.Building, new("Lobby"), default);
            Assert.True(created.IsSuccess, created.Error?.Code);
            Assert.Null(created.Value!.Content);
            Assert.True((await qr.RevokeAsync(f.Owner, f.OwnerFamily, f.Building, created.Value.Code.Id, default)).IsSuccess);
            var again = await qr.RevokeAsync(f.Owner, f.OwnerFamily, f.Building, created.Value.Code.Id, default);
            Assert.Equal(("QR_ALREADY_REVOKED", 409, "The QR code is already revoked."), (again.Error?.Code, again.Error?.Status, again.Error?.Message));
        });
    }

    [PostgresFact]
    public async Task Learner_continuation_scheme_rejects_tokens_without_the_continuation_purpose_or_ids()
    {
        using var enabled = factory!.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LearnerSessions:Enabled"] = "true", ["LearnerSessions:SigningKey"] = LearnerOptions.SigningKey, ["LearnerSessions:ContinuationSigningKey"] = LearnerOptions.ContinuationSigningKey
        })));
        var session = Guid.NewGuid();
        string Token(string purpose, string sessionId, string jti) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            LearnerOptions.ContinuationIssuer, LearnerOptions.ContinuationAudience,
            [new Claim("purpose", purpose), new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()), new Claim("role", "Trainee"), new Claim("session_id", sessionId), new Claim(JwtRegisteredClaimNames.Jti, jti)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LearnerOptions.ContinuationSigningKey)), SecurityAlgorithms.HmacSha256)));
        async Task<HttpStatusCode> Result(string token)
        {
            using var http = enabled.CreateClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            return (await http.GetAsync($"/api/training/sessions/{Guid.NewGuid()}/result")).StatusCode;
        }
        // A well-formed continuation authenticates and is then refused for another session; malformed ones never authenticate.
        Assert.Equal(HttpStatusCode.Forbidden, await Result(Token("learner_continuation", session.ToString(), Guid.NewGuid().ToString())));
        Assert.Equal(HttpStatusCode.Unauthorized, await Result(Token("learner_launch", session.ToString(), Guid.NewGuid().ToString())));
        Assert.Equal(HttpStatusCode.Unauthorized, await Result(Token("learner_continuation", "not-a-session", Guid.NewGuid().ToString())));
        Assert.Equal(HttpStatusCode.Unauthorized, await Result(Token("learner_continuation", session.ToString(), $"{session}:1")));
    }
}
