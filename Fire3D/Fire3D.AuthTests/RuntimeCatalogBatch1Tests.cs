using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#101 (17/10/2026): the batch 1 fixture is the exact body PlatformAdmin posts for Unity runtime 2.0.0. The API must accept it
    // and offer every capability contract to editors, so publish/start are not blocked by a contract the parser drops.
    [PostgresFact]
    public async Task Batch1_runtime_catalog_fixture_registers_and_offers_every_capability()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var body = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EditorContractFixtures.ContractDirectory, "fixtures", "runtime-catalog.batch1.json")))!.AsObject();
        body.Remove("_comment");
        var expected = body["capabilities"]!.AsArray().Select(x => x!.GetValue<string>()).Order().ToList();

        var created = await client.PostAsJsonAsync("/api/admin/runtime-catalog", body);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var runtime = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        Assert.Equal("2.0.0", runtime["runtimeVersion"]!.GetValue<string>());
        Assert.Equal(expected, runtime["capabilities"]!.AsArray().Select(x => x!.GetValue<string>()).Order());

        var catalog = await client.GetStringAsync("/api/scenario-interactions/catalog");
        foreach (var capability in expected)
            Assert.Contains($"\"{capability}\"", catalog);
        Assert.Equal(expected.Count, Convert.ToInt32(await ScalarAsync("SELECT count(*) FROM runtime_compatibility_catalog r, jsonb_object_keys(r.capability_contracts) WHERE r.runtime_version='2.0.0'")));
    }
}
