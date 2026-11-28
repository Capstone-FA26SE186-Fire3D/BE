using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.AuthTests;

// BE#213 (FR-PLAN-04/05/06): fire infrastructure per revision, versioned and locked by thẩm định; scenarios build on a locked version.
public sealed partial class AuthIntegrationTests
{
    private static JsonObject FixtureObject(string id) =>
        EditorContractFixtures.State()["objects"]!.AsArray().Single(o => o!["id"]!.GetValue<string>() == id)!.DeepClone().AsObject();

    private static JsonObject InfrastructureBody(Guid artifact, params (string Id, string Category)[] items)
    {
        var array = new JsonArray();
        foreach (var (id, category) in items) { var o = FixtureObject(id); o["category"] = category; array.Add(o); }
        return new JsonObject
        {
            ["geometry"] = new JsonObject { ["artifactId"] = artifact.ToString(), ["sha256Hash"] = IfcHash },
            ["runtimeVersion"] = EditorContractFixtures.State()["runtimeVersion"]!.GetValue<string>(),
            ["items"] = array
        };
    }

    private static HttpRequestMessage InfrastructureRequest(HttpMethod method, string url, string? ifMatch, string? key, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<string?> ProblemCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())?["code"]?.GetValue<string>();

    // Draft -> PUT exit and extinguisher -> submit -> an expert approves. Returns the approved version.
    private async Task<JsonObject> ApprovedInfrastructure(HttpClient org, Guid revision, Guid artifact)
    {
        var created = await org.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/revisions/{revision}/fire-infrastructures", null, "infra-" + Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var put = await org.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/fire-infrastructures/{id}", created.Headers.ETag!.Tag, null,
            InfrastructureBody(artifact, ("goal-exit", "Exit"), ("ext-1", "Equipment"))));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var submitted = await org.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/fire-infrastructures/{id}/submit", put.Headers.ETag!.Tag, "submit-" + id));
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());
        var expert = await SharedExpert();
        var approved = await expert.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/expert/reviews/{id}/approve", submitted.Headers.ETag!.Tag, "approve-" + id, new JsonObject()));
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
        return (await org.GetFromJsonAsync<JsonObject>($"/api/fire-infrastructures/{id}"))!;
    }

    // BE#242: items are assigned round-robin at submission, so a test that approves several items uses one expert for all of them.
    private HttpClient? sharedExpert;
    private async Task<HttpClient> SharedExpert() => sharedExpert ??= await RoleClient("FireSafetyExpert");

    private async Task<Guid> CreateScenarioOn(HttpClient org, Guid building)
    {
        var scenario = await org.SendAsync(Keyed(HttpMethod.Post, "/api/scenarios", new { buildingId = building, name = "Tình huống trên hạ tầng" }, "scenario-" + Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, scenario.StatusCode);
        return (await scenario.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
    }

    [PostgresFact]
    public async Task Fire_infrastructure_is_versioned_locked_by_thẩm_định_and_required_for_new_scenarios()
    {
        var g = await SeedEditorGeometry();
        await SeedEditorCatalog();
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{g.Revision}'"))!;
        using var user = await RoleClient("OrganizationUser", org);

        var first = await user.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/revisions/{g.Revision}/fire-infrastructures", null, "infra-v1"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var draft = (await first.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = draft["id"]!.GetValue<Guid>();
        Assert.Equal((1, "Draft"), (draft["version"]!.GetValue<int>(), draft["status"]!.GetValue<string>()));
        // Replay returns the same version; a second open version is refused.
        Assert.Equal(id, (await (await user.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/revisions/{g.Revision}/fire-infrastructures", null, "infra-v1")))
            .Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>());
        var second = await user.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/revisions/{g.Revision}/fire-infrastructures", null, "infra-v1b"));
        Assert.Equal(("FIRE_INFRASTRUCTURE_OPEN_VERSION_EXISTS", HttpStatusCode.Conflict), (await ProblemCode(second), second.StatusCode));

        // Item rules: an Exit is a Goal; a scenario item is not infrastructure.
        var wrong = InfrastructureBody(g.Artifact, ("ext-1", "Exit"));
        var refused = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/fire-infrastructures/{id}", first.Headers.ETag!.Tag, null, wrong));
        Assert.Equal(("FIRE_INFRASTRUCTURE_INVALID", (HttpStatusCode)422), (await ProblemCode(refused), refused.StatusCode));
        var put = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/fire-infrastructures/{id}", first.Headers.ETag!.Tag, null,
            InfrastructureBody(g.Artifact, ("goal-exit", "Exit"), ("ext-1", "Equipment"))));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var validation = (await (await user.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/fire-infrastructures/{id}/validate", null, null))).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(validation["isValid"]!.GetValue<bool>(), validation.ToJsonString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/fire-infrastructures/{id}", first.Headers.ETag!.Tag, null,
            InfrastructureBody(g.Artifact, ("goal-exit", "Exit"))))).StatusCode);

        // A scenario needs an infrastructure, and only an Approved one.
        var scenario = await CreateScenarioOn(user, g.Building);
        var none = await user.SendAsync(Keyed(HttpMethod.Post, $"/api/scenarios/{scenario}/draft", new { revisionId = g.Revision }, "draft-none"));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        var onDraft = await user.SendAsync(Keyed(HttpMethod.Post, $"/api/scenarios/{scenario}/draft", new { revisionId = g.Revision, infrastructureId = id }, "draft-on-draft"));
        Assert.Equal(("FIRE_INFRASTRUCTURE_NOT_APPROVED", (HttpStatusCode)422), (await ProblemCode(onDraft), onDraft.StatusCode));

        // Submit freezes the content; the expert approves; Approved is the lock.
        var submitted = await user.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/fire-infrastructures/{id}/submit", put.Headers.ETag!.Tag, "infra-submit"));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var afterSubmit = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/fire-infrastructures/{id}", submitted.Headers.ETag!.Tag, null, InfrastructureBody(g.Artifact, ("goal-exit", "Exit"))));
        Assert.Equal(("FIRE_INFRASTRUCTURE_NOT_DRAFT", HttpStatusCode.Conflict), (await ProblemCode(afterSubmit), afterSubmit.StatusCode));
        var expert = await SharedExpert();
        var queue = (await expert.GetFromJsonAsync<JsonObject>("/api/expert/reviews?kind=Infrastructure"))!;
        var item = queue["items"]!.AsArray().Single(x => x!["reviewId"]!.GetValue<Guid>() == id)!;
        Assert.Equal(("Infrastructure", 1), (item["kind"]!.GetValue<string>(), item["versionNumber"]!.GetValue<int>()));
        var detail = await expert.GetAsync($"/api/expert/reviews/{id}");
        Assert.Equal(2, (await detail.Content.ReadFromJsonAsync<JsonObject>())!["content"]!["items"]!.AsArray().Count);
        // Organization users never reach the expert routes.
        Assert.Equal(HttpStatusCode.Forbidden, (await user.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/expert/reviews/{id}/approve", detail.Headers.ETag!.Tag, "org-approve", new JsonObject()))).StatusCode);
        var approved = await expert.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/expert/reviews/{id}/approve", detail.Headers.ETag!.Tag, "expert-approve", new JsonObject()));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await expert.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/expert/reviews/{id}/reject", detail.Headers.ETag!.Tag, "expert-late", new JsonObject { ["reason"] = "trễ" }))).StatusCode);
        var locked = await user.GetAsync($"/api/fire-infrastructures/{id}");
        Assert.Equal("Approved", (await locked.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.GetValue<string>());
        var put409 = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/fire-infrastructures/{id}", locked.Headers.ETag!.Tag, null, InfrastructureBody(g.Artifact, ("goal-exit", "Exit"))));
        Assert.Equal(("FIRE_INFRASTRUCTURE_LOCKED", HttpStatusCode.Conflict), (await ProblemCode(put409), put409.StatusCode));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync($"UPDATE fire_infrastructures SET items='[]' WHERE id='{id}'"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync($"DELETE FROM fire_infrastructures WHERE id='{id}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM organization_notifications WHERE fire_infrastructure_id='{id}' AND notification_type='FireInfrastructureApproved'"));

        // A fix is a new version copied from the locked one, reviewed again; the locked version stays as it was.
        var v2 = await user.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/revisions/{g.Revision}/fire-infrastructures", null, "infra-v2"));
        Assert.Equal(HttpStatusCode.Created, v2.StatusCode);
        var copy = (await v2.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal((2, "Draft", 2), (copy["version"]!.GetValue<int>(), copy["status"]!.GetValue<string>(), copy["items"]!.AsArray().Count));

        // An infrastructure of another revision is refused for this revision.
        var other = await SeedEditorGeometry();
        var otherOrg = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{other.Revision}'"))!;
        using var otherUser = await RoleClient("OrganizationUser", otherOrg);
        var foreign = await ApprovedInfrastructure(otherUser, other.Revision, other.Artifact);
        var mismatch = await user.SendAsync(Keyed(HttpMethod.Post, $"/api/scenarios/{scenario}/draft", new { revisionId = g.Revision, infrastructureId = foreign["id"]!.GetValue<Guid>() }, "draft-foreign"));
        Assert.Equal(("FIRE_INFRASTRUCTURE_REVISION_MISMATCH", (HttpStatusCode)422), (await ProblemCode(mismatch), mismatch.StatusCode));
        // Another organization does not see this infrastructure at all.
        Assert.Equal(HttpStatusCode.NotFound, (await otherUser.GetAsync($"/api/fire-infrastructures/{id}")).StatusCode);
    }

    [PostgresFact]
    public async Task Scenarios_on_one_infrastructure_freeze_the_same_equipment_and_hold_none_themselves()
    {
        var g = await SeedEditorGeometry();
        await SeedEditorCatalog();
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{g.Revision}'"))!;
        using var user = await RoleClient("OrganizationUser", org);
        var infrastructure = await ApprovedInfrastructure(user, g.Revision, g.Artifact);
        var infrastructureId = infrastructure["id"]!.GetValue<Guid>();

        var versions = new List<Guid>();
        foreach (var name in new[] { "in-hours", "off-hours" })
        {
            var scenario = await CreateScenarioOn(user, g.Building);
            var created = await user.SendAsync(Keyed(HttpMethod.Post, $"/api/scenarios/{scenario}/draft", new { revisionId = g.Revision, infrastructureId }, "draft-" + name));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var draft = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
            var read = await user.GetAsync($"/api/scenario-drafts/{draft}");
            Assert.Equal(infrastructureId, (await read.Content.ReadFromJsonAsync<JsonObject>())!["infrastructureId"]!.GetValue<Guid>());

            // The scenario itself may not hold exits or fixed equipment.
            var withExit = PinnedState(g.Artifact);
            var put = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/scenario-drafts/{draft}", read.Headers.ETag!.Tag, null, withExit));
            Assert.Equal(("SCENARIO_CONTAINS_INFRASTRUCTURE", (HttpStatusCode)422), (await ProblemCode(put), put.StatusCode));
            // BE#215: a PC06 situation also names its audience, assumption and working hours.
            var own = PlanState(g.Artifact, "Evacuee", name == "in-hours" ? "InHours" : "OffHours");
            put = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/scenario-drafts/{draft}", read.Headers.ETag!.Tag, null, own));
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            var validation = (await (await user.PostAsync($"/api/scenario-drafts/{draft}/validate", null)).Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.True(validation["isValid"]!.GetValue<bool>(), validation.ToJsonString());
            var snapshot = await user.SendAsync(Snapshot(draft, put.Headers.ETag!.Tag, "snapshot-" + name));
            Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
            versions.Add((await snapshot.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>());
        }

        // Both frozen versions carry the infrastructure's exit and equipment (the package worker reads state_snapshot unchanged).
        var lists = new List<string>();
        foreach (var version in versions)
        {
            Assert.Equal(infrastructureId, (Guid)(await ScalarAsync($"SELECT infrastructure_id FROM scenario_versions WHERE id='{version}'"))!);
            lists.Add((string)(await ScalarAsync($"""
                SELECT jsonb_agg(o ORDER BY o->>'id')::text FROM scenario_versions v, jsonb_array_elements(v.state_snapshot->'objects') o
                WHERE v.id='{version}' AND o->>'kind' IN('Equipment','Goal')
                """))!);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM scenario_versions WHERE id='{version}' AND jsonb_array_length(goal_config)=1 AND scenario_hash=fet3d_jsonb_payload_hash(state_snapshot)"));
        }
        Assert.Equal(lists[0], lists[1]);
        Assert.Contains("ext-1", lists[0]);
        Assert.DoesNotContain("category", lists[0]);
        // The infrastructure counts its scenarios.
        Assert.Equal(2, (await user.GetFromJsonAsync<JsonObject>($"/api/fire-infrastructures/{infrastructureId}"))!["scenarioDraftCount"]!.GetValue<int>());
    }

    // BE#224: door.exit_locked / door.fire_wedged on an IFC door are situation states: a draft on an infrastructure may hold them,
    // while fixed doors (door.exit) and exits stay infrastructure.
    [PostgresFact]
    public async Task Locked_exit_and_wedged_fire_door_belong_to_the_situation()
    {
        static JsonObject Door(string id, string capability) => new()
        {
            ["id"] = id, ["kind"] = "Equipment", ["anchorId"] = "1hOSvn6df7F8_7GcBWlRGR",
            ["capability"] = new JsonObject { ["id"] = capability, ["version"] = "1.0.0" },
            ["placement"] = new JsonObject { ["floorId"] = "L1", ["position"] = new JsonObject { ["x"] = 8, ["y"] = 0, ["z"] = 1 }, ["rotation"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["w"] = 1 } }
        };
        var state = new JsonObject { ["objects"] = new JsonArray(Door("locked", "door.exit_locked"), Door("wedged", "door.fire_wedged"), Door("fixed", "door.exit")) };
        var conflicts = JsonNode.Parse((string)(await ScalarAsync($"SELECT scenario_infrastructure_conflicts('{state.ToJsonString()}'::jsonb)::text"))!)!.AsArray();
        Assert.Equal("$.objects[2]", Assert.Single(conflicts)!["path"]!.GetValue<string>());

        // Through the API: a draft on an Approved infrastructure saves a locked exit door.
        var g = await SeedEditorGeometry();
        await SeedEditorCatalog();
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{g.Revision}'"))!;
        using var user = await RoleClient("OrganizationUser", org);
        var infrastructure = (await ApprovedInfrastructure(user, g.Revision, g.Artifact))["id"]!.GetValue<Guid>();
        var scenario = await CreateScenarioOn(user, g.Building);
        var created = await user.SendAsync(Keyed(HttpMethod.Post, $"/api/scenarios/{scenario}/draft", new { revisionId = g.Revision, infrastructureId = infrastructure }, "door-draft"));
        var draft = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var read = await user.GetAsync($"/api/scenario-drafts/{draft}");
        var own = PinnedState(g.Artifact);
        var objects = own["objects"]!.AsArray();
        foreach (var o in objects.Where(o => o!["id"]!.GetValue<string>() is "goal-exit" or "ext-1").ToList()) objects.Remove(o);
        objects.Add(Door("locked", "door.exit_locked"));
        var put = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/scenario-drafts/{draft}", read.Headers.ETag!.Tag, null, own));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        objects.Add(Door("fixed", "door.exit"));
        var refused = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/scenario-drafts/{draft}", put.Headers.ETag!.Tag, null, own));
        Assert.Equal(("SCENARIO_CONTAINS_INFRASTRUCTURE", (HttpStatusCode)422), (await ProblemCode(refused), refused.StatusCode));
    }
}
