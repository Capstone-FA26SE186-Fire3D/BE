using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Administration;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<HttpClient> OwnerClient(Guid owner)
    {
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{owner}'");
        var http = factory!.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync((string)(await ScalarAsync($"SELECT email FROM users WHERE id='{owner}'"))!)).AccessToken);
        return http;
    }

    [PostgresFact]
    public async Task Draft_dry_run_validates_unsaved_state_without_touching_the_draft()
    {
        var f = await SeedReadyPackage(); var other = await SeedReadyPackage();
        var draft = (Guid)(await ScalarAsync($"SELECT d.id FROM scenario_drafts d JOIN scenario_versions v ON v.scenario_id=d.scenario_id WHERE v.id='{f.Version}'"))!;
        using var http = await OwnerClient(f.Owner);
        var stored = await http.GetAsync($"/api/scenario-drafts/{draft}");
        var etag = stored.Headers.ETag!.ToString();
        var state = JsonNode.Parse(await stored.Content.ReadAsStringAsync())!["state"]!;
        async Task<ScenarioDraftDryRunResponse> DryRun(string body)
        {
            var response = await http.PostAsync($"/api/scenario-drafts/{draft}/validate-state", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ScenarioDraftDryRunResponse>(Json))!;
        }
        // The stored (valid) state passes; the hash identifies exactly what was checked.
        var valid = await DryRun(state.ToJsonString());
        Assert.True(valid.IsValid, string.Join(",", valid.Issues.Select(x => x.Code)));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJsonString()))), valid.StateHash);
        // A minimal versioned state is accepted by PUT but reported incomplete here; shape problems come back as issues, not 422.
        var minimal = await DryRun("""{"schemaVersion":"fet3d.editor/1"}""");
        Assert.False(minimal.IsValid); Assert.NotEmpty(minimal.Issues);
        // Versioned documents are strict: an unknown field is a shape issue (legacy drafts keep their typed projection).
        var shape = await DryRun("""{"schemaVersion":"fet3d.editor/1","notAField":1}""");
        Assert.False(shape.IsValid); Assert.Contains(shape.Issues, x => x.Path.Contains("notAField"));
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"/api/scenario-drafts/{draft}/validate-state", new StringContent("[]", Encoding.UTF8, "application/json"))).StatusCode);
        var malformed = await http.PostAsync($"/api/scenario-drafts/{draft}/validate-state", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal("EDITOR_JSON_MALFORMED", (await malformed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        // Nothing was stored: same revision and ETag.
        Assert.Equal(etag, (await http.GetAsync($"/api/scenario-drafts/{draft}")).Headers.ETag!.ToString());
        Assert.Equal(etag, $"\"{minimal.StoredVersion}\"");
        using var stranger = await OwnerClient(other.Owner);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/scenario-drafts/{draft}/validate-state", new StringContent(state.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
    }

    [PostgresFact]
    public async Task Validation_history_marks_current_runs_and_revision_status_aggregates_jobs()
    {
        var f = await SeedReadyPackage(); var other = await SeedReadyPackage();
        using var http = await OwnerClient(f.Owner);
        var history = await http.GetFromJsonAsync<PageResponse<ValidationRunHistoryItem>>($"/api/revisions/{f.Revision}/validation-runs", Json);
        var run = Assert.Single(history!.Items, x => x.Id == f.Run);
        Assert.True(run.IsCurrent); Assert.Equal("Passed", run.Outcome); Assert.Equal("ReleasePackage", run.JobKind); Assert.Equal("Succeeded", run.AttemptStatus);
        Assert.Equal(1, (await http.GetFromJsonAsync<PageResponse<ValidationRunHistoryItem>>($"/api/revisions/{f.Revision}/validation-runs?jobId={run.ProcessingJobId}", Json))!.TotalCount);
        Assert.Equal(0, (await http.GetFromJsonAsync<PageResponse<ValidationRunHistoryItem>>($"/api/revisions/{f.Revision}/validation-runs?jobId={Guid.NewGuid()}", Json))!.TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"/api/revisions/{f.Revision}/validation-runs?pageSize=101")).StatusCode);

        // The current QA read hides a run once its job no longer counts; history keeps it, marked historical.
        await ExecuteAsync($"ALTER TABLE processing_jobs DISABLE TRIGGER USER;UPDATE processing_jobs SET status='Failed' WHERE id='{run.ProcessingJobId}';ALTER TABLE processing_jobs ENABLE TRIGGER USER");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/validation-runs/{f.Run}")).StatusCode);
        var historical = Assert.Single((await http.GetFromJsonAsync<PageResponse<ValidationRunHistoryItem>>($"/api/revisions/{f.Revision}/validation-runs", Json))!.Items, x => x.Id == f.Run);
        Assert.False(historical.IsCurrent);

        var readiness = await http.GetFromJsonAsync<RevisionReadiness>($"/api/revisions/{f.Revision}/readiness", Json);
        var package = Assert.Single(readiness!.LatestJobs!, x => x.Kind == "ReleasePackage");
        Assert.Equal(run.ProcessingJobId, package.JobId); Assert.Equal("Failed", package.Status); Assert.Equal(f.Version, package.ScenarioVersionId);

        using var stranger = await OwnerClient(other.Owner);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/revisions/{f.Revision}/validation-runs")).StatusCode);
    }
}
