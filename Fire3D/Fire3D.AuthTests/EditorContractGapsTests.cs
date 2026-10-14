using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Issue #90: stateHash covers the bytes the client sent (Vietnamese included), floors carry semantic anchors,
    // draft rows say whether a draft is still empty, and ProblemDetails documents its code/errors/issues.
    [PostgresFact]
    public async Task Editor_state_hash_floors_anchors_and_draft_kinds_follow_the_contract()
    {
        var g = await SeedEditorGeometry();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var (draft, _) = await CreateEditorDraft(g.Building, g.Revision);

        // The client hashes the exact string it sends; the server must not re-encode "Thoát hiểm" as \u escapes.
        const string body = """{"schemaVersion":"fet3d.editor/1","name":"Thoát hiểm tầng 3 <A&B>"}""";
        var dryRun = await client.PostAsync($"/api/scenario-drafts/{draft}/validate-state", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, dryRun.StatusCode);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        Assert.Equal(expected, (await dryRun.Content.ReadFromJsonAsync<JsonObject>())!["stateHash"]!.GetValue<string>());

        var floors = await client.GetFromJsonAsync<JsonObject>($"/api/revisions/{g.Revision}/floors");
        Assert.Equal("Ready", floors!["status"]!.GetValue<string>());
        Assert.NotNull(floors["semanticMapping"]);
        Assert.Equal("door_101", floors["semanticMapping"]!.AsArray()[0]!["nodeId"]!.GetValue<string>());

        var scenario = (Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_drafts WHERE id='{draft}'"))!;
        async Task<string> Kind() => (await client.GetFromJsonAsync<JsonObject>($"/api/scenarios/{scenario}/drafts"))!["items"]![0]!["stateKind"]!.GetValue<string>();
        Assert.Equal("Uninitialized", await Kind());
        await ExecuteAsync($"UPDATE scenario_drafts SET state='{{\"spawnPoints\":[]}}'::jsonb WHERE id='{draft}'");
        Assert.Equal("Legacy", await Kind());
        await ExecuteAsync($"UPDATE scenario_drafts SET state='{{\"schemaVersion\":\"fet3d.editor/1\"}}'::jsonb WHERE id='{draft}'");
        Assert.Equal("Versioned", await Kind());

        var openApi = await client.GetFromJsonAsync<JsonObject>("/openapi/v1.json");
        var problem = openApi!["components"]!["schemas"]!["ProblemDetails"]!["properties"]!.AsObject();
        Assert.True(problem.ContainsKey("code") && problem.ContainsKey("errors") && problem.ContainsKey("issues"));
    }
}
