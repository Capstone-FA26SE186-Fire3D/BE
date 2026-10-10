using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<(Guid Revision, Guid Building, Guid Artifact, Guid Run)> SeedEditorGeometry(JsonNode? metadata = null, string outcome = "Passed")
    {
        var f = await FinalReviewGeometry(outcome, [], metadata ?? EditorContractFixtures.Geometry());
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        var artifact = (Guid)(await ScalarAsync($"SELECT id FROM revision_artifacts WHERE job_id='{f.Job}' AND artifact_type='preview_glb'"))!;
        return (f.Revision, building, artifact, f.Run);
    }

    private async Task SeedEditorCatalog()
    {
        var c = EditorContractFixtures.CatalogFixture();
        await ExecuteAsync($"""
            INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities,is_active,created_at,capability_contracts)
            VALUES(gen_random_uuid(),'{c["runtimeVersion"]}','{c["protocolVersion"]}','{c["manifestSchemaVersion"]}','{c["capabilities"]!.ToJsonString()}'::jsonb,true,now(),'{c["capabilityContracts"]!.ToJsonString()}'::jsonb)
            """);
    }

    private async Task<(Guid Draft, string ETag)> CreateEditorDraft(Guid building, Guid revision)
    {
        var scenario = await client.SendAsync(Keyed(HttpMethod.Post, "/api/scenarios", new { buildingId = building, name = "Editor contract" }, "editor-scenario-" + revision));
        Assert.Equal(HttpStatusCode.Created, scenario.StatusCode);
        var scenarioId = (await scenario.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var draft = await client.SendAsync(Keyed(HttpMethod.Post, $"/api/scenarios/{scenarioId}/draft", new { revisionId = revision }, "editor-draft-" + revision));
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var draftId = (await draft.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var read = await client.GetAsync($"/api/scenario-drafts/{draftId}");
        return (draftId, read.Headers.ETag!.Tag);
    }

    private static HttpRequestMessage Keyed(HttpMethod method, string url, object body, string key)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private Task<HttpResponseMessage> PutDraft(Guid draft, string etag, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/scenario-drafts/{draft}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static JsonObject PinnedState(Guid artifact)
    {
        var state = EditorContractFixtures.State();
        state["geometry"] = new JsonObject { ["artifactId"] = artifact.ToString(), ["sha256Hash"] = IfcHash };
        return state;
    }

    [PostgresFact]
    public async Task Editor_v1_draft_round_trips_validates_references_and_snapshots_projection()
    {
        var g = await SeedEditorGeometry();
        await SeedEditorCatalog();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var (draft, etag) = await CreateEditorDraft(g.Building, g.Revision);

        // Malformed JSON, unsupported version and unknown fields are distinct and never change the stored draft.
        var malformed = await PutDraft(draft, etag, "{\"schemaVersion\":");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("EDITOR_JSON_MALFORMED", (await malformed.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        var unsupported = await PutDraft(draft, etag, """{"schemaVersion":"fet3d.editor/9"}""");
        Assert.Equal((HttpStatusCode)422, unsupported.StatusCode);
        Assert.Equal("EDITOR_SCHEMA_VERSION_UNSUPPORTED", (await unsupported.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        var unknown = PinnedState(g.Artifact); unknown["objects"]![0]!.AsObject()["color"] = "red";
        var rejected = await PutDraft(draft, etag, unknown.ToJsonString());
        Assert.Equal((HttpStatusCode)422, rejected.StatusCode);
        var problem = (await rejected.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("EDITOR_SCHEMA_INVALID", problem["code"]!.GetValue<string>());
        Assert.Contains(problem["issues"]!.AsArray(), x => x!["path"]!.GetValue<string>() == "$.objects[0].color" && x["code"]!.GetValue<string>() == "FIELD_UNKNOWN");
        Assert.NotNull(problem["traceId"]);
        Assert.Equal(etag, (await client.GetAsync($"/api/scenario-drafts/{draft}")).Headers.ETag!.Tag);

        // GET -> PUT -> GET keeps every supported field of every object kind.
        var state = PinnedState(g.Artifact);
        var saved = await PutDraft(draft, etag, state.ToJsonString());
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        etag = saved.Headers.ETag!.Tag;
        var reopened = (await client.GetFromJsonAsync<JsonObject>($"/api/scenario-drafts/{draft}"))!;
        Assert.True(JsonNode.DeepEquals(state, reopened["state"]), reopened["state"]!.ToJsonString());
        var again = await PutDraft(draft, etag, reopened["state"]!.ToJsonString());
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        etag = again.Headers.ETag!.Tag;
        Assert.True(JsonNode.DeepEquals(state, (await client.GetFromJsonAsync<JsonObject>($"/api/scenario-drafts/{draft}"))!["state"]));

        var validation = (await (await client.PostAsync($"/api/scenario-drafts/{draft}/validate", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True(validation["isValid"]!.GetValue<bool>(), validation.ToJsonString());

        // Reference errors come from the database function that the snapshot gate reuses.
        var broken = PinnedState(g.Artifact);
        broken["objects"]![0]!["placement"]!["floorId"] = "L9";
        broken["objects"]![1]!["placement"]!["floorId"] = "L2";
        broken["objects"]![4]!["anchorId"] = "0000000000000000000000";
        broken["objects"]![2]!["parameters"]!["intensity"] = 11;
        var put = await PutDraft(draft, etag, broken.ToJsonString());
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        etag = put.Headers.ETag!.Tag;
        var invalid = (await (await client.PostAsync($"/api/scenario-drafts/{draft}/validate", null)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.False(invalid["isValid"]!.GetValue<bool>());
        var issues = invalid["issues"]!.AsArray().Select(x => (x!["code"]!.GetValue<string>(), x["path"]!.GetValue<string>())).ToList();
        Assert.Contains(("FLOOR_NOT_FOUND", "$.objects[0].placement.floorId"), issues);
        Assert.Contains(("ANCHOR_FLOOR_MISMATCH", "$.objects[1].anchorId"), issues);
        Assert.Contains(("ANCHOR_NOT_FOUND", "$.objects[4].anchorId"), issues);
        Assert.Contains(("PARAMETER_OUT_OF_RANGE", "$.objects[2].parameters.intensity"), issues);
        var blocked = await client.SendAsync(Snapshot(draft, etag, "editor-snapshot-broken"));
        Assert.Equal((HttpStatusCode)422, blocked.StatusCode);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM scenario_versions WHERE revision_id='{g.Revision}'"));

        put = await PutDraft(draft, etag, state.ToJsonString());
        etag = put.Headers.ETag!.Tag;
        var snapshot = await client.SendAsync(Snapshot(draft, etag, "editor-snapshot"));
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        var version = (await snapshot.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        Assert.Equal(1L, await ScalarAsync($$"""
            SELECT count(*) FROM scenario_versions WHERE id='{{version}}' AND scenario_hash=fet3d_jsonb_payload_hash(state_snapshot)
             AND jsonb_array_length(spawn_config)=1 AND jsonb_array_length(goal_config)=1 AND jsonb_array_length(fire_source_config)=1
             AND jsonb_array_length(npc_config)=1 AND jsonb_array_length(blocked_elements)=1 AND time_limit_seconds=600
             AND routing_config='{"goalObjectIds":["goal-exit"]}'::jsonb AND random_seed=42 AND replan_interval_seconds=5
             AND mode_policy=state_snapshot->'modePolicy'
            """));
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/scenario-versions/{version}"))!;
        Assert.True(JsonNode.DeepEquals(state, detail["stateSnapshot"] ?? detail["state"]), detail.ToJsonString());
    }

    private static HttpRequestMessage Snapshot(Guid draft, string etag, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/scenario-drafts/{draft}/snapshot");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [PostgresFact]
    public async Task Editor_floors_catalog_and_legacy_geometry_are_reported_without_fake_coordinates()
    {
        var v1 = await SeedEditorGeometry();
        var legacy = await SeedEditorGeometry(JsonNode.Parse("""{"objectAnchors":["door-1"],"coordinateTransform":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],"floors":[],"semanticMapping":{}}"""));
        await SeedEditorCatalog();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);

        var floors = (await client.GetFromJsonAsync<JsonObject>($"/api/revisions/{v1.Revision}/floors"))!;
        Assert.Equal("Ready", floors["status"]!.GetValue<string>());
        Assert.Equal("fet3d.editor/1", floors["schemaVersion"]!.GetValue<string>());
        Assert.Equal(["L1", "L2"], floors["floors"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));
        Assert.Equal(3.5, floors["floors"]![1]!["elevationMeters"]!.GetValue<double>());

        var old = (await client.GetFromJsonAsync<JsonObject>($"/api/revisions/{legacy.Revision}/floors"))!;
        Assert.Equal("ReprocessRequired", old["status"]!.GetValue<string>());
        Assert.Null(old["floors"]);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/revisions/{Guid.NewGuid()}/floors")).StatusCode);

        // A versioned draft cannot pin a legacy artifact or one from another revision.
        var (draft, etag) = await CreateEditorDraft(legacy.Building, legacy.Revision);
        Assert.Equal(HttpStatusCode.NoContent, (await PutDraft(draft, etag, PinnedState(legacy.Artifact).ToJsonString())).StatusCode);
        var issues = (await (await client.PostAsync($"/api/scenario-drafts/{draft}/validate", null)).Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray();
        Assert.Contains(issues, x => x!["code"]!.GetValue<string>() == "GEOMETRY_NOT_ACCEPTED");
        etag = (await client.GetAsync($"/api/scenario-drafts/{draft}")).Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NoContent, (await PutDraft(draft, etag, PinnedState(v1.Artifact).ToJsonString())).StatusCode);
        issues = (await (await client.PostAsync($"/api/scenario-drafts/{draft}/validate", null)).Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray();
        Assert.Contains(issues, x => x!["code"]!.GetValue<string>() == "GEOMETRY_NOT_ACCEPTED");

        var catalog = (await client.GetFromJsonAsync<JsonArray>("/api/scenario-interactions/catalog"))!;
        var runtime = catalog.Single(x => x!["runtimeVersion"]!.GetValue<string>() == "1.4.0")!;
        Assert.Equal(["equipment.extinguisher", "fire.source", "npc.evacuee"], runtime["capabilityContracts"]!.AsObject().Select(x => x.Key));
    }

    [PostgresFact]
    public async Task Editor_snapshot_rechecks_geometry_acceptance_and_legacy_drafts_are_unchanged()
    {
        var g = await SeedEditorGeometry();
        await SeedEditorCatalog();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var (draft, etag) = await CreateEditorDraft(g.Building, g.Revision);
        var put = await PutDraft(draft, etag, PinnedState(g.Artifact).ToJsonString());
        etag = put.Headers.ETag!.Tag;
        // Geometry QA now has a blocking issue: the pinned artifact is no longer accepted.
        await ExecuteAsync($"INSERT INTO revision_issues(id,validation_run_id,severity,code,message,details,created_at) VALUES(gen_random_uuid(),'{g.Run}','Error','LATE_QA','Blocking fixture','{{}}',now())");
        var blocked = await client.SendAsync(Snapshot(draft, etag, "editor-snapshot-qa"));
        Assert.Equal((HttpStatusCode)422, blocked.StatusCode);
        Assert.Contains("GEOMETRY_NOT_ACCEPTED", await blocked.Content.ReadAsStringAsync());

        // Legacy body keeps the typed projection and legacy validator.
        var legacyBody = JsonSerializer.Serialize(ValidAuthoringState(), Json);
        var legacy = await PutDraft(draft, etag, legacyBody);
        Assert.Equal(HttpStatusCode.NoContent, legacy.StatusCode);
        var stored = (await client.GetFromJsonAsync<JsonObject>($"/api/scenario-drafts/{draft}"))!["state"]!;
        Assert.Null(stored["schemaVersion"]);
        Assert.Equal(1, stored["spawnPoints"]!.AsArray().Count);
        var snapshot = await client.SendAsync(Snapshot(draft, legacy.Headers.ETag!.Tag, "editor-snapshot-legacy"));
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        var version = (await snapshot.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        Assert.Equal("[{\"x\": 1, \"y\": 2, \"z\": 3, \"rotation\": 0}]", await ScalarAsync($"SELECT spawn_config::text FROM scenario_versions WHERE id='{version}'"));
    }

    [PostgresFact]
    public async Task Editor_migration_keeps_reference_function_owner_and_restricted_callers()
    {
        Assert.Equal("fet3d_ifc_upload_owner", await ScalarAsync("SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid='scenario_reference_issues(uuid,jsonb)'::regprocedure"));
        Assert.Equal(false, await ScalarAsync("SELECT has_function_privilege('public','scenario_reference_issues(uuid,jsonb)','EXECUTE')"));
        Assert.Equal(false, await ScalarAsync("SELECT has_function_privilege('public','project_editor_v1_snapshot()','EXECUTE')"));
        Assert.Equal(true, await ScalarAsync("SELECT column_default IS NOT NULL AND is_nullable='NO' FROM information_schema.columns WHERE table_name='runtime_compatibility_catalog' AND column_name='capability_contracts'"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync("INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities,is_active,created_at,capability_contracts) VALUES(gen_random_uuid(),'9.9.9','1','1','[]',false,now(),'[]')"));
        await WithAuthoringRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var issues = await new Fire3D.Infrastructure.Scenarios.ScenarioReadStore(db).ValidateReferencesAsync(Guid.NewGuid(), PinnedState(Guid.NewGuid()), default);
            Assert.Contains(issues, x => x.Code == "GEOMETRY_NOT_ACCEPTED");
        });
    }
}

public sealed class EditorWorkerMetadataTests
{
    private static async Task<(int Status, string? Code, int GateCalls)> Output(object metadata)
    {
        var calls = 0;
        var gate = ResetProxy.For<Fire3D.Application.Ifc.IProcessingWorkerGate>((_, _) => { calls++; return Task.FromResult(Fire3D.Application.Authentication.AuthResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(new { code = "OK" }))); });
        var controller = new Fire3D.API.Controllers.ProcessingWorkerController(gate, Microsoft.Extensions.Options.Options.Create(new Fire3D.Application.Ifc.ProcessingWorkerOptions()))
            { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        var input = JsonSerializer.SerializeToElement(new
        {
            attemptId = Guid.NewGuid(), leaseToken = Guid.NewGuid(),
            output = new { artifactType = "preview_glb", objectKey = "worker-output/x/preview.glb", schemaVersion = "1", validatorVersion = "fake", sha256Hash = new string('a', 64), sizeBytes = 10L, outcome = "Passed", metadata, issues = Array.Empty<object>() }
        });
        var result = await controller.Output(Guid.NewGuid(), input, default);
        if (result is Microsoft.AspNetCore.Mvc.OkObjectResult) return (200, null, calls);
        var problem = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(result);
        return (problem.StatusCode!.Value, (problem.Value as Microsoft.AspNetCore.Mvc.ProblemDetails)?.Extensions["code"]?.ToString(), calls);
    }

    [Fact]
    public async Task Worker_must_satisfy_declared_editor_metadata_before_acceptance()
    {
        Assert.Equal((200, null, 1), await Output(EditorContractFixtures.Geometry()));
        Assert.Equal((200, null, 1), await Output(new { objectAnchors = new[] { "door-1" } }));
        var invalid = EditorContractFixtures.Geometry(); invalid["floors"]![0]!["transform"]![0] = 0; invalid["floors"]![0]!["transform"]![5] = 0;
        Assert.Equal((422, "EDITOR_SCHEMA_INVALID", 0), await Output(invalid));
        Assert.Equal((422, "EDITOR_SCHEMA_VERSION_UNSUPPORTED", 0), await Output(new { schemaVersion = "fet3d.editor/2" }));
    }
}
