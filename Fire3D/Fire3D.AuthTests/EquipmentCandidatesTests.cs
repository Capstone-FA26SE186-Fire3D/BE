using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Fire3D.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static JsonObject BuildingMetadataFixture() => BuildingMetadataValidatorTests.Fixture();

    // A verified revision of an existing Building (the helper SeedVerifiedIfcRevision makes a new Building each time).
    private async Task<Guid> SeedVerifiedIfcRevisionIn(Guid building, string label)
    {
        var fake = new IfcMemoryStorage(); await using var context = BuildingContext(testConnection);
        var flow = new IfcUploadService(new IfcUploadStore(context), fake.Storage, fake.Inspector, IfcOptions, TimeProvider.System);
        var upload = (await flow.InitiateAsync(adminId, building, new(IfcBytes.Length, "model.ifc", label, IfcHash), Guid.NewGuid().ToString("N"), default)).Value!;
        fake.Put(upload.ObjectKey, IfcBytes); Assert.True((await flow.CompleteAsync(adminId, upload.RevisionId, CompleteInput(upload), default)).IsSuccess);
        return upload.RevisionId;
    }

    // Fake worker: one Geometry attempt with preview_glb (editor metadata) and building_metadata (detected equipment).
    private async Task<Guid> SeedEquipmentGeometry(Guid revision, JsonObject building, object[]? previewIssues = null)
    {
        Guid run = default;
        await WithProcessingRuntime(async (api, worker) =>
        {
            await using var db = BuildingContext(api);
            var store = new ProcessingRuntimeStore(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }).Build());
            var job = (await store.RequestAsync(adminId, revision, "equipment-" + revision, default)).Value;
            var delivery = await store.ExecuteAsync("Claim", null, null, null, default);
            var claimed = (await store.ExecuteAsync("Claim", job, WorkerInput(new { eventKey = delivery.GetProperty("eventKey").GetString(), payloadHash = delivery.GetProperty("payloadHash").GetString(), inputHash = IfcHash, toolchainVersion = "fake-ifc-equipment" }), default)).Value;
            var attemptId = claimed.GetProperty("attemptId").GetGuid(); var leaseToken = claimed.GetProperty("leaseToken").GetGuid(); var prefix = claimed.GetProperty("outputPrefix").GetString();
            string? hash = null;
            foreach (var (type, metadata) in new[] { ("preview_glb", (JsonNode)EditorContractFixtures.Geometry()), ("building_metadata", building) })
            {
                var output = new { artifactType = type, objectKey = prefix + type + ".json", sha256Hash = IfcHash, sizeBytes = 100L, schemaVersion = "1", metadata, validatorVersion = "fake", outcome = "Passed", issues = type == "preview_glb" ? previewIssues ?? [] : Array.Empty<object>() };
                var registered = await store.ExecuteAsync("Output", job, WorkerInput(new { attemptId, leaseToken, output }), default);
                Assert.True(registered.IsSuccess, registered.Error?.Code); hash = registered.Value.GetProperty("outputHash").GetString();
            }
            var completed = await store.ExecuteAsync("Complete", job, WorkerInput(new { attemptId, leaseToken, outputHash = hash }), default);
            Assert.True(completed.IsSuccess, completed.Error?.Code); run = completed.Value.GetProperty("validationRunId").GetGuid();
        });
        return run;
    }

    // IFC-04: candidates come from building_metadata of the accepted Geometry attempt; decisions are an ETag'd overlay that never
    // touches artifacts, accept only Equipment capabilities of an active runtime, and are suggested again on a later revision.
    [PostgresFact]
    public async Task Equipment_candidates_are_confirmed_with_etags_and_suggested_on_the_next_revision()
    {
        var revision = await SeedVerifiedIfcRevision();
        var run = await SeedEquipmentGeometry(revision, BuildingMetadataFixture());
        Assert.Equal("preview_glb", (string?)await ScalarAsync($"SELECT a.artifact_type FROM validation_runs v JOIN revision_artifacts a ON a.id=v.candidate_artifact_id WHERE v.id='{run}'"));
        await SeedEditorCatalog();
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM buildings WHERE id='{building}'"))!;
        var (user, family) = await SeedOrganizationUser(org);
        var stranger = await SeedAiTenant();
        var login = "equipment_runtime_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION revision_equipment_gate(text,uuid,uuid,uuid,jsonb,bigint) TO {login}");
        try
        {
            await using var db = BuildingContext(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString);
            var gate = new EquipmentCandidates(db);
            var view = (await gate.GetAsync(user, family, revision, default)).Value!;
            Assert.True(view.MetadataAvailable); Assert.Equal(0, view.Version);
            Assert.Equal(new[] { false, true }, view.Items.Select(i => i.NeedsConfirmation));
            Assert.Equal("equipment.extinguisher", view.Items[0].CatalogKey);
            Assert.Equal(new[] { 12.3m, 0.9m, 4.5m }, view.Items[0].Position);
            Assert.Equal("NOT_FOUND", (await gate.GetAsync(stranger.User, stranger.Family, revision, default)).Error?.Code);

            EquipmentDecisionsRequest Decide(params EquipmentDecisionInput[] items) => new(items);
            var extinguisher = view.Items[0].Id; var sink = view.Items[1].Id;
            Assert.Equal("PRECONDITION_FAILED", (await gate.SaveAsync(user, family, revision, 3, Decide(new EquipmentDecisionInput(extinguisher, "Confirmed")), default)).Error?.Code);
            var invalid = await gate.SaveAsync(user, family, revision, 0, Decide(new EquipmentDecisionInput("ifc:missing", "Confirmed") , new EquipmentDecisionInput(sink, "Confirmed")), default);
            Assert.Equal("EQUIPMENT_DECISION_INVALID", invalid.Error?.Code);
            Assert.Equal(2, invalid.Error!.Errors!.Count);
            var artifacts = await ScalarAsync($"SELECT count(*) FROM revision_artifacts WHERE revision_id='{revision}'");

            var saved = await gate.SaveAsync(user, family, revision, 0, Decide(new EquipmentDecisionInput(extinguisher, "Confirmed") , new EquipmentDecisionInput(sink, "Rejected", Note: "Không phải nguồn nước")), default);
            Assert.True(saved.IsSuccess, saved.Error?.Code);
            Assert.Equal(1, saved.Value!.Version);
            Assert.Equal(("Confirmed", "equipment.extinguisher"), (saved.Value.Items[0].Decision!.Decision, saved.Value.Items[0].Decision!.CatalogKey));
            Assert.Equal(("Rejected", (string?)null), (saved.Value.Items[1].Decision!.Decision, saved.Value.Items[1].Decision!.CatalogKey));
            // Merge: changing one candidate keeps the other's decision.
            var merged = (await gate.SaveAsync(user, family, revision, 1, Decide(new EquipmentDecisionInput(extinguisher, "Retyped", "equipment.extinguisher")), default)).Value!;
            Assert.Equal((2L, "Retyped", "Rejected"), (merged.Version, merged.Items[0].Decision!.Decision, merged.Items[1].Decision!.Decision));
            Assert.Equal(artifacts, await ScalarAsync($"SELECT count(*) FROM revision_artifacts WHERE revision_id='{revision}'"));
            Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='RevisionEquipmentDecisions' AND target_id='{revision}'"));
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE revision_equipment_decisions SET data='{{\"items\":[]}}' WHERE revision_id='{revision}'"));

            // A new revision of the Building: no decisions yet, but the same GlobalIds come back as suggestions.
            var next = await SeedVerifiedIfcRevisionIn(building, "v2");
            await SeedEquipmentGeometry(next, BuildingMetadataFixture());
            var fresh = (await gate.GetAsync(user, family, next, default)).Value!;
            Assert.Equal(0, fresh.Version);
            Assert.All(fresh.Items, i => Assert.Null(i.Decision));
            Assert.Equal(("Retyped", revision), (fresh.Items[0].SuggestedDecision!.Decision, fresh.Items[0].SuggestedDecision!.FromRevisionId));
        }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }

        // HTTP: OrganizationUser/PlatformAdmin only; If-Match required; ETag is the overlay version.
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var got = await client.GetAsync($"/api/revisions/{revision}/equipment-candidates");
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("\"2\"", got.Headers.ETag!.Tag);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/revisions/{revision}/equipment-decisions") { Content = JsonContent.Create(new { items = new[] { new { candidateId = "x", decision = "Maybe" } } }) };
        Assert.Equal((HttpStatusCode)428, (await client.SendAsync(patch)).StatusCode);
        patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/revisions/{revision}/equipment-decisions") { Content = JsonContent.Create(new { items = new[] { new { candidateId = "x", decision = "Maybe" } } }) };
        patch.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        var rejected = await client.SendAsync(patch);
        Assert.Equal((HttpStatusCode)422, rejected.StatusCode);
        Assert.Contains("decision", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/revisions/{Guid.NewGuid()}/equipment-candidates")).StatusCode);
    }
}

public sealed class BuildingMetadataValidatorTests
{
    // contracts/ifc/v1 sits beside contracts/editor/v1, found the same way as the editor fixtures.
    public static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(Path.Combine(EditorContractFixtures.ContractDirectory, "..", "..", "ifc", "v1", "fixtures", "building-metadata.valid.json")))!.AsObject();

    [Fact]
    public void Building_metadata_validator_accepts_the_fixture_and_names_bad_fields()
    {
        Assert.Empty(BuildingMetadataValidator.Validate(Fixture()));
        var bad = Fixture();
        bad["extra"] = 1;
        bad["interactables"]![0]!["confidence"] = 1.5;
        bad["interactables"]![1]!["id"] = bad["interactables"]![0]!["id"]!.GetValue<string>();
        bad["interactables"]![1]!["position"] = new JsonArray(1, 2);
        var issues = BuildingMetadataValidator.Validate(bad).Select(i => i.Path).ToList();
        Assert.Contains("$.extra", issues);
        Assert.Contains("$.interactables[0].confidence", issues);
        Assert.Contains("$.interactables[1].id", issues);
        Assert.Contains("$.interactables[1].position", issues);
    }
}
