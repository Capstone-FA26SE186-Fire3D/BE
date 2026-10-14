using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Content;
using Fire3D.Application.Editor;
using Fire3D.Infrastructure.Content;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Issue #88: admin filters, capability check against the active runtime catalog and typed responses.
    [PostgresFact]
    public async Task Library_admin_filters_and_publish_requires_supported_capabilities()
    {
        var admin = await AdminFamily();
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new ContentGates(db);
            async Task<JsonElement> Item(string kind, string code) =>
                (await gates.LibraryAsync("CreateItem", adminId, admin, Guid.Empty, new { kind, code }, "item-" + code, null, default)).Value;
            var extinguisher = await Item("Equipment", "EXTINGUISHER-CO2");
            var hose = await Item("Equipment", "HOSE-REEL");
            var version = (await gates.LibraryAsync("CreateVersion", adminId, admin, extinguisher.GetProperty("id").GetGuid(),
                new { name = "CO2 extinguisher", payload = JsonNode.Parse("""{"properties":{"weightKg":5}}"""), requiredCapabilities = new[] { "equipment.extinguisher" } }, "co2-v1", null, default)).Value;

            // No active runtime offers the capability yet.
            var refused = await gates.LibraryAsync("PublishVersion", adminId, admin, version.GetProperty("id").GetGuid(), new { }, null, version.GetProperty("revision").GetInt64(), default);
            Assert.Equal(("LIBRARY_CAPABILITY_UNSUPPORTED", 409), (refused.Error?.Code, refused.Error?.Status));
            await ExecuteAsync("INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities,is_active,created_at) VALUES(gen_random_uuid(),'9.9.8','1','1','[\"equipment.extinguisher\"]',false,now())");
            Assert.Equal("LIBRARY_CAPABILITY_UNSUPPORTED", (await gates.LibraryAsync("PublishVersion", adminId, admin, version.GetProperty("id").GetGuid(), new { }, null, version.GetProperty("revision").GetInt64(), default)).Error?.Code);
            await ExecuteAsync("INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities,is_active,created_at) VALUES(gen_random_uuid(),'9.9.9','1','1','[\"equipment.extinguisher\"]',true,now())");
            var published = await gates.LibraryAsync("PublishVersion", adminId, admin, version.GetProperty("id").GetGuid(), new { }, null, version.GetProperty("revision").GetInt64(), default);
            Assert.True(published.IsSuccess, published.Error?.Code);
            Assert.Equal("Published", published.Value.Deserialize<LibraryVersionResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Status);

            // Admin filters run before paging; the item representation matches the typed OpenAPI schema.
            var current = (await gates.LibraryAsync("Get", adminId, admin, hose.GetProperty("id").GetGuid(), new { admin = true }, null, null, default)).Value;
            Assert.True((await gates.LibraryAsync("UpdateItem", adminId, admin, hose.GetProperty("id").GetGuid(), new { isActive = false }, null, current.GetProperty("revision").GetInt64(), default)).IsSuccess);
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            LibraryItemPage Page(object input) => gates.LibraryAsync("List", adminId, admin, Guid.Empty, input, null, null, default).Result.Value.Deserialize<LibraryItemPage>(web)!;
            Assert.Equal(new[] { "EXTINGUISHER-CO2" }, Page(new { admin = true, isActive = true }).Items.Select(x => x.Code));
            Assert.Equal(new[] { "HOSE-REEL" }, Page(new { admin = true, isActive = false }).Items.Select(x => x.Code));
            Assert.Equal(new[] { "HOSE-REEL" }, Page(new { admin = true, search = "reel" }).Items.Select(x => x.Code));
            Assert.Empty(Page(new { admin = true, search = "%" }).Items);
            var typed = Page(new { admin = true, search = "co2" }).Items.Single();
            Assert.Equal(("Equipment", 1), (typed.Kind, typed.LatestPublished!.VersionNumber));
            Assert.Equal(new[] { "equipment.extinguisher" }, typed.LatestPublished.RequiredCapabilities);
        });
    }

    // Issue #88: an editor draft may record which Library versions it was copied from; the field is shape-checked only.
    [Fact]
    public void Editor_draft_accepts_library_provenance_and_rejects_malformed_refs()
    {
        var valid = JsonNode.Parse($$"""{"schemaVersion":"fet3d.editor/1","libraryRefs":[{"versionId":"{{Guid.NewGuid()}}","kind":"ScenarioTemplate","code":"EXIT-BASIC"}]}""");
        Assert.Empty(ScenarioStateV1Validator.ValidateShape(valid));
        var invalid = JsonNode.Parse("""{"schemaVersion":"fet3d.editor/1","libraryRefs":[{"versionId":"nope","kind":"Poster","extra":1}]}""");
        var paths = ScenarioStateV1Validator.ValidateShape(invalid).Select(x => x.Path).ToArray();
        Assert.Contains("$.libraryRefs[0].versionId", paths);
        Assert.Contains("$.libraryRefs[0].kind", paths);
        Assert.Contains(paths, p => p.StartsWith("$.libraryRefs[0]", StringComparison.Ordinal) && p.Contains("extra"));
    }
}
