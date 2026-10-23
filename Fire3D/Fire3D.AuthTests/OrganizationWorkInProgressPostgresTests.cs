using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Fire3D.Infrastructure.Scenarios;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private const string WorkInProgress = "/api/organizations/me/work-in-progress";

    private async Task<HttpClient> WorkInProgressClient(Guid user)
    {
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{user}'");
        var email = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{user}'"))!;
        var http = factory!.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginAsync(email)).AccessToken);
        return http;
    }

    private static async Task<JsonElement[]> WorkItems(HttpClient http, string query = "")
    {
        var response = await http.GetAsync(WorkInProgress + query);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().ToArray();
    }

    private async Task<Guid> SeedOwner(Guid organization)
    {
        var owner = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO users(password_hash,id,organization_id,email,full_name,role,is_active,email_verified_at,created_at,updated_at) VALUES('fixture-hash','{owner}','{organization}','owner-{owner:N}@example.test','Owner','OrganizationUser',true,now(),now(),now())");
        return owner;
    }

    [PostgresFact]
    public async Task Work_in_progress_lists_latest_saved_drafts_of_active_tenant_Buildings_only()
    {
        // Organization A: a submitted snapshot on scenario 1, a second draft of it, a fresh scenario 2 and a draft on a second Building.
        var f = await SeedReadyPackage();
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{f.Revision}'"))!;
        var scenario1 = (Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;
        var draft1 = (Guid)(await ScalarAsync($"SELECT id FROM scenario_drafts WHERE scenario_id='{scenario1}'"))!;
        await using var db = BuildingContext(testConnection);
        Assert.True((await new ScenarioReadinessStore(db).ExecuteAsync("Submit", f.Owner, f.Owner, f.Version, null, new { }, "wip-submit", default)).IsSuccess);
        var authoring = new ScenarioWriteStore(db);
        var scenario2 = (await authoring.CreateScenarioAsync(f.Owner, building, org, new(building, "Second drill"), default, "wip-scenario-2")).Value;
        var draft2 = (await authoring.CreateScenarioDraftAsync(f.Owner, scenario2, org, new(f.Revision), default, "wip-draft-2")).Value;
        var draft3 = (await authoring.CreateScenarioDraftAsync(f.Owner, scenario1, org, new(f.Revision), default, "wip-draft-3")).Value;
        var building2 = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{building2}','{org}','Annex',true,'{adminId}',now(),now())");
        var revision2 = await SeedVerifiedIfcRevisionIn(building2, "annex");
        var scenario4 = (await authoring.CreateScenarioAsync(f.Owner, building2, org, new(building2, "Annex drill"), default, "wip-scenario-4")).Value;
        var draft4 = (await authoring.CreateScenarioDraftAsync(f.Owner, scenario4, org, new(revision2), default, "wip-draft-4")).Value;
        await ExecuteAsync($"""
            UPDATE scenario_drafts SET updated_at=now()-interval '30 minutes' WHERE id='{draft4}';
            UPDATE scenario_drafts SET updated_at=now()-interval '1 hour' WHERE id='{draft1}';
            UPDATE scenario_drafts SET updated_at=now()-interval '2 hours' WHERE id='{draft2}';
            UPDATE scenario_drafts SET updated_at=now()-interval '3 hours' WHERE id='{draft3}';
            """);

        // Organization B has a newer draft; an empty organization has none.
        var otherRevision = await SeedVerifiedIfcRevision();
        var otherBuilding = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{otherRevision}'"))!;
        var otherOrg = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{otherRevision}'"))!;
        var otherOwner = await SeedOwner(otherOrg);
        var otherScenario = (await authoring.CreateScenarioAsync(otherOwner, otherBuilding, otherOrg, new(otherBuilding, "Foreign drill"), default, "wip-foreign")).Value;
        var otherDraft = (await authoring.CreateScenarioDraftAsync(otherOwner, otherScenario, otherOrg, new(otherRevision), default, "wip-foreign-draft")).Value;
        await ExecuteAsync($"UPDATE scenario_drafts SET updated_at=now()+interval '1 hour' WHERE id='{otherDraft}'");
        var emptyOwner = await SeedOwner(await SeedBuildingOrganization());

        using var owner = await WorkInProgressClient(f.Owner);
        var top = Assert.Single(await WorkItems(owner));
        Assert.Equal(["kind", "buildingId", "buildingName", "scenarioId", "scenarioName", "draftId", "revisionId", "latestVersionNumber", "latestVersionReviewStatus", "updatedAt"],
            top.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(("ScenarioDraft", draft4, building2, "Annex", scenario4, "Annex drill", revision2), (top.GetProperty("kind").GetString(), top.GetProperty("draftId").GetGuid(),
            top.GetProperty("buildingId").GetGuid(), top.GetProperty("buildingName").GetString(), top.GetProperty("scenarioId").GetGuid(), top.GetProperty("scenarioName").GetString(),
            top.GetProperty("revisionId").GetGuid()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (top.GetProperty("latestVersionNumber").ValueKind, top.GetProperty("latestVersionReviewStatus").ValueKind));

        var all = await WorkItems(owner, "?limit=5");
        Assert.Equal([draft4, draft1, draft2, draft3], all.Select(x => x.GetProperty("draftId").GetGuid()).ToArray());
        Assert.True(all.Zip(all.Skip(1)).All(pair => pair.First.GetProperty("updatedAt").GetDateTime() > pair.Second.GetProperty("updatedAt").GetDateTime()));
        // Drafts of a scenario that already has a snapshot are included with that snapshot's number and review status.
        foreach (var item in new[] { all[1], all[3] })
            Assert.Equal((1, "Submitted", scenario1), (item.GetProperty("latestVersionNumber").GetInt32(), item.GetProperty("latestVersionReviewStatus").GetString(), item.GetProperty("scenarioId").GetGuid()));
        Assert.Equal(JsonValueKind.Null, all[2].GetProperty("latestVersionReviewStatus").ValueKind);
        Assert.DoesNotContain(otherDraft, all.Select(x => x.GetProperty("draftId").GetGuid()));

        // Archived or deleted Buildings drop out.
        await ExecuteAsync($"UPDATE buildings SET is_active=false WHERE id='{building2}'");
        Assert.Equal([draft1, draft2, draft3], (await WorkItems(owner, "?limit=5")).Select(x => x.GetProperty("draftId").GetGuid()).ToArray());
        await ExecuteAsync($"UPDATE buildings SET is_active=true,deleted_at=now() WHERE id='{building2}'");
        Assert.Equal(draft1, Assert.Single(await WorkItems(owner)).GetProperty("draftId").GetGuid());

        // A real save moves the draft to the top.
        var xmin = Convert.ToUInt32(await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft2}'"));
        Assert.True((await authoring.UpdateScenarioDraftAsync(f.Owner, draft2, xmin, ValidAuthoringState(), org, default)).IsSuccess);
        Assert.Equal(draft2, Assert.Single(await WorkItems(owner, "?limit=1")).GetProperty("draftId").GetGuid());

        foreach (var limit in new[] { "0", "6", "-1" })
        {
            var invalid = await owner.GetAsync(WorkInProgress + "?limit=" + limit);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("VALIDATION_ERROR", problem.GetProperty("code").GetString()); Assert.True(problem.GetProperty("errors").TryGetProperty("limit", out _));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(WorkInProgress + "?limit=abc")).StatusCode);

        using var other = await WorkInProgressClient(otherOwner);
        Assert.Equal(otherDraft, Assert.Single(await WorkItems(other, "?limit=5")).GetProperty("draftId").GetGuid());
        using var empty = await WorkInProgressClient(emptyOwner);
        Assert.Empty(await WorkItems(empty, "?limit=5"));

        var trainee = await SeedTrainee();
        using var traineeClient = await WorkInProgressClient(trainee.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await traineeClient.GetAsync(WorkInProgress)).StatusCode);
        using var admin = factory!.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginAsync()).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(WorkInProgress)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory!.CreateClient().GetAsync(WorkInProgress)).StatusCode);

        // A revoked session family is rejected.
        await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{f.Owner}'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(WorkInProgress)).StatusCode);
    }
}

public sealed class OrganizationWorkInProgressOpenApiTests
{
    [BillingPostgresFact]
    public async Task Work_in_progress_contract_is_published_for_OrganizationUser_with_limit_and_item_schema()
    {
        await using var database = await BillingDatabase.Create(migrationHistory: true); using var factory = new BillingApiTests.Factory(database); using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json")); var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/api/organizations/me/work-in-progress").GetProperty("get");
        Assert.Contains("Roles: OrganizationUser", operation.GetProperty("description").GetString());
        Assert.NotEmpty(operation.GetProperty("security").EnumerateArray());
        var limit = Assert.Single(operation.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "limit");
        Assert.Equal("query", limit.GetProperty("in").GetString());
        foreach (var status in new[] { "200", "400", "401", "403" }) Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), status);
        Assert.Contains("OrganizationWorkInProgressResponse", operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetRawText());
        var item = root.GetProperty("components").GetProperty("schemas").GetProperty("WorkInProgressItemResponse").GetProperty("properties");
        foreach (var field in new[] { "kind", "buildingId", "buildingName", "scenarioId", "scenarioName", "draftId", "revisionId", "latestVersionNumber", "latestVersionReviewStatus", "updatedAt" })
            Assert.True(item.TryGetProperty(field, out _), field);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/organizations/me/work-in-progress")).StatusCode);
    }
}
