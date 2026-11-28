using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Fire3D.AuthTests;

// BE#215 (FR-PLAN-07, FR-PLAN-12): PC06 situations carry audience, assumption and working hours; publishing needs Approved in-hours and
// off-hours situations on the infrastructure; the plan dossier follows the order of Mẫu PC06.
public sealed partial class AuthIntegrationTests
{
    private static JsonObject PlanState(Guid artifact, string? audience = "Evacuee", string? workingHours = "InHours")
    {
        var state = PinnedState(artifact);
        var objects = state["objects"]!.AsArray();
        foreach (var o in objects.Where(o => o!["id"]!.GetValue<string>() is "goal-exit" or "ext-1").ToList()) objects.Remove(o);
        if (audience is not null) state["audience"] = audience;
        if (workingHours is not null) state["workingHours"] = workingHours;
        if (audience is not null)
            state["assumption"] = new JsonObject { ["time"] = workingHours == "OffHours" ? "23:00" : "10:00", ["location"] = "Phòng 101", ["cause"] = "Chập điện", ["peoplePresent"] = 40 };
        return state;
    }

    // Draft on the infrastructure -> PUT state -> snapshot; returns the version.
    private async Task<Guid> PlanVersion(HttpClient org, Guid building, Guid revision, Guid infrastructure, JsonObject state)
    {
        var scenario = await CreateScenarioOn(org, building);
        var created = await org.SendAsync(Keyed(HttpMethod.Post, $"/api/scenarios/{scenario}/draft", new { revisionId = revision, infrastructureId = infrastructure }, "plan-draft-" + Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var read = await org.GetAsync($"/api/scenario-drafts/{draft}");
        var put = await org.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/scenario-drafts/{draft}", read.Headers.ETag!.Tag, null, state));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var snapshot = await org.SendAsync(Snapshot(draft, put.Headers.ETag!.Tag, "plan-snapshot-" + draft));
        Assert.True(snapshot.StatusCode == HttpStatusCode.Created, await snapshot.Content.ReadAsStringAsync());
        return (await snapshot.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
    }

    private async Task ApproveScenarioVersion(HttpClient org, Guid version)
    {
        var submitted = await org.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/scenario-versions/{version}/submit", null, "submit-" + version, new JsonObject()));
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());
        var review = (Guid)(await ScalarAsync($"SELECT id FROM scenario_content_reviews WHERE scenario_version_id='{version}'"))!;
        var expert = await SharedExpert();
        var approved = await expert.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/expert/reviews/{review}/approve", "\"1\"", "approve-" + review, new JsonObject()));
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task Pc06_situations_need_audience_assumption_and_hours_and_the_dossier_follows_the_form()
    {
        var g = await SeedEditorGeometry();
        await SeedEditorCatalog();
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{g.Revision}'"))!;
        await ExecuteAsync($"UPDATE buildings SET building_type='Apartment',total_floors=6,facility_head_name='Nguyễn Văn A' WHERE id='{g.Building}'");
        using var user = await RoleClient("OrganizationUser", org);
        var infrastructure = (await ApprovedInfrastructure(user, g.Revision, g.Artifact))["id"]!.GetValue<Guid>();

        // A situation without audience/assumption/workingHours saves, but validate reports what is missing; a wrong value is a shape error.
        var scenario = await CreateScenarioOn(user, g.Building);
        var created = await user.SendAsync(Keyed(HttpMethod.Post, $"/api/scenarios/{scenario}/draft", new { revisionId = g.Revision, infrastructureId = infrastructure }, "plan-missing"));
        var draft = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var etag = (await user.GetAsync($"/api/scenario-drafts/{draft}")).Headers.ETag!.Tag;
        var bare = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/scenario-drafts/{draft}", etag, null, PlanState(g.Artifact, null, null)));
        Assert.Equal(HttpStatusCode.NoContent, bare.StatusCode);
        var validation = (await (await user.PostAsync($"/api/scenario-drafts/{draft}/validate", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(validation["isValid"]!.GetValue<bool>());
        var paths = validation["issues"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()).ToList();
        Assert.Contains("$.audience", paths); Assert.Contains("$.workingHours", paths); Assert.Contains("$.assumption", paths);
        Assert.Equal((HttpStatusCode)422, (await user.SendAsync(Snapshot(draft, bare.Headers.ETag!.Tag, "plan-missing-snapshot"))).StatusCode);
        var wrong = PlanState(g.Artifact, "Boss");
        var refused = await user.SendAsync(InfrastructureRequest(HttpMethod.Put, $"/api/scenario-drafts/{draft}", bare.Headers.ETag!.Tag, null, wrong));
        Assert.Equal(("EDITOR_SCHEMA_INVALID", (HttpStatusCode)422), (await ProblemCode(refused), refused.StatusCode));

        // In-hours situation approved by an expert, off-hours one not yet submitted.
        var inHours = await PlanVersion(user, g.Building, g.Revision, infrastructure, PlanState(g.Artifact, "Evacuee", "InHours"));
        await ApproveScenarioVersion(user, inHours);
        await PlanVersion(user, g.Building, g.Revision, infrastructure, PlanState(g.Artifact, "FirstResponse", "OffHours"));

        var dossier = (await user.GetFromJsonAsync<JsonObject>($"/api/buildings/{g.Building}/plan-dossier"))!;
        Assert.Equal(infrastructure, dossier["siteLayout"]!["infrastructureId"]!.GetValue<Guid>());
        Assert.Single(dossier["siteLayout"]!["exits"]!.AsArray());
        Assert.Equal("Nguyễn Văn A", dossier["facility"]!["facilityHeadName"]!.GetValue<string>());
        var equipment = dossier["facility"]!["equipment"]!.AsArray().Single()!;
        Assert.Equal(("equipment.extinguisher", 1), (equipment["catalogKey"]!.GetValue<string>(), equipment["count"]!.GetValue<int>()));
        Assert.Equal(("Apartment", "Group2"), (dossier["hazard"]!["buildingType"]!.GetValue<string>(), dossier["hazard"]!["riskGroup"]!.GetValue<string>()));
        // The scenario without a snapshot is not listed; the two situations are numbered 1, 2 with their PC06 fields.
        var scenarios = dossier["scenarios"]!.AsArray();
        Assert.Equal([1, 2], scenarios.Select(s => s!["number"]!.GetValue<int>()));
        Assert.Equal(("Evacuee", "InHours", "Approved"), (scenarios[0]!["audience"]!.GetValue<string>(), scenarios[0]!["workingHours"]!.GetValue<string>(), scenarios[0]!["reviewStatus"]!.GetValue<string>()));
        Assert.Equal(40, scenarios[0]!["assumption"]!["peoplePresent"]!.GetValue<int>());
        Assert.Equal(("OffHours", "NotSubmitted"), (scenarios[1]!["workingHours"]!.GetValue<string>(), scenarios[1]!["reviewStatus"]!.GetValue<string>()));
        Assert.Equal((true, false, false), (dossier["coverage"]!["inHours"]!.GetValue<bool>(), dossier["coverage"]!["offHours"]!.GetValue<bool>(), dossier["coverage"]!["complete"]!.GetValue<bool>()));
        Assert.Single(dossier["history"]!.AsArray());

        // Another organization's Building is not found; an infrastructure of another Building is refused.
        var other = await SeedEditorGeometry();
        var otherOrg = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{other.Revision}'"))!;
        using var otherUser = await RoleClient("OrganizationUser", otherOrg);
        Assert.Equal(HttpStatusCode.NotFound, (await otherUser.GetAsync($"/api/buildings/{g.Building}/plan-dossier")).StatusCode);
        var foreign = (await ApprovedInfrastructure(otherUser, other.Revision, other.Artifact))["id"]!.GetValue<Guid>();
        var mismatch = await user.GetAsync($"/api/buildings/{g.Building}/plan-dossier?infrastructureId={foreign}");
        Assert.Equal(("FIRE_INFRASTRUCTURE_NOT_APPROVED", (HttpStatusCode)422), (await ProblemCode(mismatch), mismatch.StatusCode));
        // A Building without an Approved infrastructure has an empty plan.
        var empty = (await otherUser.GetFromJsonAsync<JsonObject>($"/api/buildings/{other.Building}/plan-dossier?infrastructureId={foreign}"))!;
        Assert.Empty(empty["scenarios"]!.AsArray());
    }

    // Publish of a version on an infrastructure needs Approved InHours and OffHours situations on that infrastructure (FR-PLAN-07).
    [PostgresFact]
    public async Task Publish_needs_approved_in_hours_and_off_hours_situations_on_the_infrastructure()
    {
        var f = await SeedReadyPackage();
        var input = await ApproveRelease(f);
        var family = await SeedPlaytestFamily(f.Owner);
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{f.Revision}'"))!;
        await SeedPublishPaidEntitlement(f.Owner, building);
        // The release's version is put on an Approved infrastructure (fixture: the immutable trigger is bypassed for this test only).
        var infrastructure = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO fire_infrastructures(id,revision_id,building_id,organization_id,version_number,status,row_revision,items,content_hash,created_by,submitted_by,submitted_at,decided_by,decided_at)
            VALUES('{infrastructure}','{f.Revision}','{building}','{org}',1,'Approved',3,'[]','{new string('a', 64)}','{f.Owner}','{f.Owner}',now(),'{f.Owner}',now());
            ALTER TABLE scenario_versions DISABLE TRIGGER immutable_scenario_snapshot;
            UPDATE scenario_versions SET infrastructure_id='{infrastructure}' WHERE id='{f.Version}';
            ALTER TABLE scenario_versions ENABLE TRIGGER immutable_scenario_snapshot;
            """);
        async Task AddApprovedSituation(string hours)
        {
            var id = Guid.NewGuid();
            await ExecuteAsync($$"""
                INSERT INTO scenario_versions(id,scenario_id,revision_id,building_id,organization_id,version_number,name,schema_version,algorithm_version,random_seed,time_limit_seconds,spawn_config,goal_config,fire_source_config,npc_config,blocked_elements,routing_config,scoring_config,mode_policy,safety_thresholds,replan_interval_seconds,scenario_hash,created_by,created_at,state_snapshot,learning_objectives,learner_instructions,rubric,infrastructure_id)
                SELECT '{{id}}',scenario_id,revision_id,building_id,organization_id,version_number+100+(SELECT count(*) FROM scenario_versions WHERE infrastructure_id='{{infrastructure}}'),name,schema_version,algorithm_version,random_seed,time_limit_seconds,spawn_config,goal_config,fire_source_config,npc_config,blocked_elements,routing_config,scoring_config,mode_policy,safety_thresholds,replan_interval_seconds,
                 fet3d_jsonb_payload_hash(state_snapshot||'{"workingHours":"{{hours}}"}'),created_by,now(),state_snapshot||'{"workingHours":"{{hours}}"}',learning_objectives,learner_instructions,rubric,'{{infrastructure}}'
                FROM scenario_versions WHERE id='{{f.Version}}';
                INSERT INTO scenario_content_reviews(id,scenario_version_id,status,content_hash,rubric_hash,submitted_by,submitted_at,reviewed_by,reviewed_at)
                SELECT gen_random_uuid(),v.id,'Approved',v.scenario_hash,fet3d_jsonb_payload_hash(v.rubric),'{{f.Owner}}',now(),'{{f.Owner}}',now() FROM scenario_versions v WHERE v.id='{{id}}';
                """);
        }
        await WithReleaseRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var built = await new Fire3D.Infrastructure.Releases.ReleaseWriteStore(db, TimeProvider.System).BuildAsync(f.Owner, null, input, default, "plan-build", family);
            Assert.True(built.IsSuccess, built.Error?.Code);
            JsonElement Publish(string key) => JsonDocument.Parse((string)ScalarAsync($"SELECT publish_release_gate('Publish','{f.Owner}','{family}','{built.Value!.Id}','{{}}','{key}',NULL)::text").Result!).RootElement;

            var none = Publish("plan-1");
            Assert.Equal("PLAN_SCENARIO_COVERAGE_INCOMPLETE", none.GetProperty("code").GetString());
            Assert.Equal(["InHours", "OffHours"], none.GetProperty("errors").GetProperty("workingHours").EnumerateArray().Select(x => x.GetString()));
            await AddApprovedSituation("InHours");
            var onlyIn = Publish("plan-2");
            Assert.Equal(("PLAN_SCENARIO_COVERAGE_INCOMPLETE", 409), (onlyIn.GetProperty("code").GetString(), onlyIn.GetProperty("status").GetInt32()));
            Assert.Equal(["OffHours"], onlyIn.GetProperty("errors").GetProperty("workingHours").EnumerateArray().Select(x => x.GetString()));
            await AddApprovedSituation("OffHours");
            Assert.Equal("Published", Publish("plan-3").GetProperty("result").GetProperty("status").GetString());
        });
    }
}
