using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.AuthTests;

// BE#231: an AI scenario draft is a situation on a fire infrastructure (BE#213, BE#215, BE#224). The request pins an Approved version and
// sends its items to the AI service in bim.infrastructure; the draft keeps no exit or fixed equipment of its own.
public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Ai_scenario_draft_on_a_fire_infrastructure_sends_its_items_and_keeps_no_exit_of_its_own()
    {
        var admin = await AdminFamily();
        var g = await SeedEditorGeometry();
        await SeedEditorCatalog();
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM buildings WHERE id='{g.Building}'"))!;
        var (user, family) = await SeedOrganizationUser(org);
        await SeedAiGrant(org, 1000);
        using var web = await RoleClient("OrganizationUser", org);

        // An empty infrastructureId is refused before anything is reserved.
        using var empty = new HttpRequestMessage(HttpMethod.Post, "/api/ai/organization/scenario-draft")
        { Content = JsonContent.Create(new { buildingId = g.Building, prompt = "Cháy bếp tầng 1", infrastructureId = Guid.Empty }) };
        empty.Headers.Add("Idempotency-Key", "empty-infrastructure");
        var refused = await web.SendAsync(empty);
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Contains("$.infrastructureId", await refused.Content.ReadAsStringAsync());

        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            var source = await ApprovedSource(gates, admin);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("ScenarioDraft", "tokens", 200, 2000, 5), "draft-policy", null, default)).IsSuccess);
            object Draft(Guid? revision = null, Guid? infrastructure = null) => infrastructure is null
                ? new { operation = "ScenarioDraft", request = new { buildingId = g.Building, revisionId = revision, prompt = "Cháy bếp tầng 1", sources = Array.Empty<object>() } }
                : new { operation = "ScenarioDraft", request = new { buildingId = g.Building, revisionId = revision, infrastructureId = infrastructure, prompt = "Cháy bếp tầng 1", sources = Array.Empty<object>() } };
            // The service places an exit (goal-exit) and an extinguisher (ext-1) among six objects, as before BE#231.
            var fake = new FakeFastApi { Respond = (_, body) => FakeFastApi.Json(DraftResponse(body!["request_id"]!.ToString(), g.Artifact, g.Revision, source)) };

            // No Approved fire infrastructure yet: a legacy request, its draft kept as the service sent it.
            var legacy = await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(), "legacy", default);
            Assert.True(legacy.IsSuccess, legacy.Error?.Code);
            Assert.Equal(JsonValueKind.Null, legacy.Value.GetProperty("input").GetProperty("infrastructureId").ValueKind);
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Null(fake.Calls.Last().Body!["bim"]!["infrastructure"]);
            var legacyResult = (await gates.OrganizationAsync("Get", user, family, legacy.Value.GetProperty("requestId").GetGuid(), new { }, null, default)).Value.GetProperty("result");
            Assert.Equal(6, legacyResult.GetProperty("draft").GetProperty("state").GetProperty("objects").GetArrayLength());
            Assert.Equal(new[] { "An expert must check the site." }, legacyResult.GetProperty("limitations").EnumerateArray().Select(x => x.GetString()!).ToArray());

            // An expert approves version 1 (exit and extinguisher); version 2 stays Draft.
            var infrastructure = (await ApprovedInfrastructure(web, g.Revision, g.Artifact))["id"]!.GetValue<Guid>();
            var open = await web.SendAsync(InfrastructureRequest(HttpMethod.Post, $"/api/revisions/{g.Revision}/fire-infrastructures", null, "infra-v2"));
            Assert.Equal(HttpStatusCode.Created, open.StatusCode);
            var draftVersion = (await open.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();

            // A version of another Building or revision, or one not Approved, is refused as CreateDraft refuses it, with the field named.
            var foreign = await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(infrastructure: Guid.NewGuid()), "foreign-infra", default);
            Assert.Equal(("FIRE_INFRASTRUCTURE_REVISION_MISMATCH", 422), (foreign.Error?.Code, foreign.Error?.Status));
            Assert.True(foreign.Error!.Errors!.ContainsKey("infrastructureId"));
            Assert.Equal("FIRE_INFRASTRUCTURE_REVISION_MISMATCH", (await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(Guid.NewGuid(), infrastructure), "other-revision", default)).Error?.Code);
            var unapproved = await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(infrastructure: draftVersion), "draft-infra", default);
            Assert.Equal(("FIRE_INFRASTRUCTURE_NOT_APPROVED", 422), (unapproved.Error?.Code, unapproved.Error?.Status));

            // Without infrastructureId the newest Approved version of the pinned revision is used (not the open Draft);
            // with infrastructureId alone its revision is pinned.
            var pinned = await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(g.Revision), "on-infrastructure", default);
            Assert.True(pinned.IsSuccess, pinned.Error?.Code);
            var pinnedId = pinned.Value.GetProperty("requestId").GetGuid();
            Assert.Equal(infrastructure, pinned.Value.GetProperty("input").GetProperty("infrastructureId").GetGuid());
            var explicitPin = await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(infrastructure: infrastructure), "explicit-infrastructure", default);
            Assert.True(explicitPin.IsSuccess, explicitPin.Error?.Code);
            Assert.Equal((g.Revision, infrastructure), (explicitPin.Value.GetProperty("input").GetProperty("revisionId").GetGuid(), explicitPin.Value.GetProperty("input").GetProperty("infrastructureId").GetGuid()));
            Assert.Equal(infrastructure.ToString(), (string?)await ScalarAsync($"SELECT new_values->>'infrastructureId' FROM audit_logs WHERE target_id='{pinnedId}' AND target_entity='AiRequest'"));

            // Admin sees what goes to the service: id, version and the items without positions.
            var bim = (await gates.AdminAsync("GetRequest", adminId, admin, pinnedId, new { }, null, null, default)).Value.GetProperty("bim").GetProperty("infrastructure");
            Assert.Equal((infrastructure, 1), (bim.GetProperty("id").GetGuid(), bim.GetProperty("version").GetInt32()));
            Assert.Equal(new[] { "goal-exit/Goal/Exit", "ext-1/Equipment/Equipment" },
                bim.GetProperty("items").EnumerateArray().Select(i => $"{i.GetProperty("id").GetString()}/{i.GetProperty("kind").GetString()}/{i.GetProperty("category").GetString()}").ToArray());
            Assert.Equal("equipment.extinguisher", bim.GetProperty("items")[1].GetProperty("capability").GetProperty("id").GetString());
            Assert.All(bim.GetProperty("items").EnumerateArray(), i => Assert.False(i.GetProperty("placement").TryGetProperty("position", out _)));

            // The service still places the exit and the extinguisher: both are removed, and the first limitations say so.
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal(infrastructure.ToString(), fake.Calls.Last().Body!["bim"]!["infrastructure"]!["id"]!.ToString());
            var drafted = (await gates.OrganizationAsync("Get", user, family, pinnedId, new { }, null, default)).Value;
            Assert.Equal("Succeeded", drafted.GetProperty("status").GetString());
            var result = drafted.GetProperty("result");
            Assert.Equal(new[] { "spawn-1", "fire-1", "npc-1", "blocked-door" },
                result.GetProperty("draft").GetProperty("state").GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()!).ToArray());
            var limitations = result.GetProperty("limitations").EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.StartsWith("Đã bỏ 2 đối tượng thuộc hạ tầng PCCC (1 lối thoát/điểm tập kết, 1 thiết bị cố định)", limitations[0]);
            Assert.StartsWith("Bản nháp chưa có audience, workingHours, assumption", limitations[1]);
            Assert.Equal("An expert must check the site.", limitations[2]);
            Assert.Equal("SCENARIO_CONTAINS_INFRASTRUCTURE,PLAN_FIELDS_REQUIRED",
                (string?)await ScalarAsync($"SELECT string_agg(r->>'ruleId',',' ORDER BY n) FROM ai_requests,jsonb_array_elements(result->'ruleResults') WITH ORDINALITY x(r,n) WHERE id='{pinnedId}'"));

            // The database refuses a draft that still holds them (a worker that skipped the removal).
            var claim = (await gates.WorkerAsync("Claim", "test-worker", null, null, new { leaseSeconds = 60 }, default)).GetProperty("result");
            var claimed = claim.GetProperty("requestId").GetGuid();
            Assert.Equal(explicitPin.Value.GetProperty("requestId").GetGuid(), claimed);
            var raw = JsonSerializer.SerializeToNode(DraftResponse(claimed.ToString(), g.Artifact, g.Revision, source))!.AsObject();
            var unfiltered = OrganizationAiInput.Completion("ScenarioDraft", claimed, raw).Completion;
            Assert.Equal("NeedsReconcile", (await gates.WorkerAsync("Complete", "test-worker", claimed, claim.GetProperty("leaseToken").GetGuid(), unfiltered, default))
                .GetProperty("result").GetProperty("status").GetString());
            Assert.Equal("NeedsReconcile/AI_RESULT_INVALID", (string?)await ScalarAsync($"SELECT status||'/'||failure_code FROM ai_requests WHERE id='{claimed}'"));
        });
    }
}

public sealed class AiDraftInfrastructureRulesTests
{
    private static JsonObject Response(Guid id) => new()
    {
        ["request_id"] = id.ToString(), ["response_type"] = "ScenarioDraft", ["status"] = "NeedsUserEdit", ["recommendation"] = "Cháy bếp tầng 1.",
        ["scenario_draft"] = new JsonObject { ["state"] = EditorContractFixtures.State() },
        ["citations"] = new JsonArray(new JsonObject { ["document_id"] = Guid.NewGuid().ToString(), ["document_version"] = Guid.NewGuid().ToString() }),
        ["limitations"] = new JsonArray("Chuyên gia cần kiểm tra."), ["usage"] = new JsonObject { ["provider_units"] = 10 }
    };

    // BE#231: only a request on a fire infrastructure changes the draft; the other one is settled as the service sent it.
    [Fact]
    public void Only_a_draft_on_a_fire_infrastructure_loses_its_exits_and_fixed_equipment()
    {
        var id = Guid.NewGuid();
        var (legacy, legacyViolation) = OrganizationAiInput.Completion("ScenarioDraft", id, Response(id));
        Assert.Null(legacyViolation);
        Assert.Equal(6, legacy["result"]!["draft"]!["state"]!["objects"]!.AsArray().Count);
        Assert.Equal(new[] { "Chuyên gia cần kiểm tra." }, legacy["result"]!["limitations"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());

        var (onInfrastructure, violation) = OrganizationAiInput.Completion("ScenarioDraft", id, Response(id), onInfrastructure: true);
        Assert.Null(violation);
        var objects = onInfrastructure["result"]!["draft"]!["state"]!["objects"]!.AsArray();
        Assert.DoesNotContain(objects, o => o!["kind"]!.GetValue<string>() is "Goal" or "Equipment");
        var limitations = onInfrastructure["result"]!["limitations"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Equal(3, limitations.Length);
        Assert.Equal("Chuyên gia cần kiểm tra.", limitations[2]);
        var rule = onInfrastructure["result"]!["ruleResults"]![0]!;
        Assert.Equal(("SCENARIO_CONTAINS_INFRASTRUCTURE", "Removed: $.objects[1] Goal, $.objects[5] Equipment equipment.extinguisher"),
            (rule["ruleId"]!.GetValue<string>(), rule["reason"]!.GetValue<string>()));
    }
}
