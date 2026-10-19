using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Releases;
using Fire3D.Infrastructure.Releases;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#130: the worker contract lets unity_package metadata add provenance next to the manifest's contract fields; release Build and
    // Publish compare only protocolVersion, manifestSchemaVersion, buildTarget, minRuntimeVersion and requiredCapabilities.
    [PostgresFact]
    public async Task Release_build_and_publish_accept_package_provenance_and_compare_contract_fields()
    {
        var f = await SeedReadyPackage(); var input = await ApproveRelease(f); var family = await SeedPlaytestFamily(f.Owner);
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        var manifest = $"(SELECT m.id FROM revision_artifacts m JOIN revision_artifacts a ON a.attempt_id=m.attempt_id WHERE a.id='{f.Artifact}' AND m.artifact_type='manifest')";
        await ExecuteAsync($$"""UPDATE revision_artifacts SET metadata=metadata||'{"agentVersion":"1.0.0","fileCount":3,"totalSizeBytes":4096}'::jsonb WHERE id='{{f.Artifact}}'""");
        await WithReleaseRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new ReleaseWriteStore(db, TimeProvider.System, Options.Create(new PublishingOptions { Enabled = true }));
            // A contract field that differs between the two outputs is still refused (runtime compatibility reads unity_package only).
            await ExecuteAsync($"UPDATE revision_artifacts SET metadata=jsonb_set(metadata,'{{protocolVersion}}','\"9\"') WHERE id={manifest}");
            Assert.Equal("PACKAGE_METADATA_MISMATCH", (await store.BuildAsync(f.Owner, null, input, default, "mismatch", family)).Error?.Code);
            await ExecuteAsync($"UPDATE revision_artifacts SET metadata=jsonb_set(metadata,'{{protocolVersion}}','\"1\"') WHERE id={manifest}");
            var built = await store.BuildAsync(f.Owner, null, input, default, "build", family); Assert.True(built.IsSuccess, built.Error?.Code);

            await SeedPublishPaidEntitlement(f.Owner, building);
            await ExecuteAsync($"UPDATE revision_artifacts SET metadata=jsonb_set(metadata,'{{protocolVersion}}','\"9\"') WHERE id={manifest}");
            Assert.Equal("PACKAGE_COMPATIBILITY_REQUIRED", (await store.PublishAsync(f.Owner, built.Value!.Id, null, default, family)).Error?.Code);
            await ExecuteAsync($"UPDATE revision_artifacts SET metadata=jsonb_set(metadata,'{{protocolVersion}}','\"1\"') WHERE id={manifest}");
            var published = await store.PublishAsync(f.Owner, built.Value.Id, null, default, family, "publish");
            Assert.True(published.IsSuccess, published.Error?.Code); Assert.Equal("Published", published.Value!.Status);
        });
    }

    // BE#131: a revision runs one job at a time (one_live_revision_job). A second build while one is Queued answered 500; a build for a
    // target no worker serves stayed Queued forever and blocked the revision.
    [PostgresFact]
    public async Task Package_build_rejects_unserved_targets_and_reports_the_live_revision_job()
    {
        var revision = await SeedVerifiedIfcRevision(); var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        await using var authoringDb = BuildingContext(testConnection); var authoring = new ScenarioWriteStore(authoringDb);
        var scenario = (await authoring.CreateScenarioAsync(adminId, building, null, new(building, "Live job"), default, "live-scenario")).Value;
        var draft = (await authoring.CreateScenarioDraftAsync(adminId, scenario, null, new(revision), default, "live-draft")).Value;
        var xmin = Convert.ToUInt32(await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft}'"));
        var updated = await authoring.UpdateScenarioDraftAsync(adminId, draft, xmin, ValidAuthoringState(), null, default);
        var version = (await authoring.SnapshotScenarioDraftAsync(adminId, draft, null, default, "live-snapshot", updated.Value)).Value;
        var job = (await authoring.BuildAsync(adminId, version, new("PlaytestPackage", "Android"), "live-build", default)).Value;

        var second = await authoring.BuildAsync(adminId, version, new("ReleasePackage", "Android"), "live-release", default);
        Assert.Equal("PROCESSING_JOB_ACTIVE", second.Error?.Code); Assert.Equal(409, second.Error?.Status);
        Assert.Equal(job.ToString(), Assert.Single(second.Error!.Errors!["jobId"]));
        // The same build input still answers its own job.
        Assert.Equal(job, (await authoring.BuildAsync(adminId, version, new("PlaytestPackage", "Android"), "live-build-again", default)).Value);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM processing_jobs WHERE revision_id='{revision}' AND status IN('Queued','Running')"));

        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        async Task<(HttpStatusCode Status, JsonElement Body)> Post(string kind, string target, string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/scenario-versions/{version}/package-builds") { Content = JsonContent.Create(new { kind, buildTarget = target }) };
            request.Headers.Add("Idempotency-Key", key);
            var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }
        var windows = await Post("PlaytestPackage", "Windows", "http-windows");
        Assert.Equal(HttpStatusCode.BadRequest, windows.Status); Assert.Equal("BUILD_TARGET_UNSUPPORTED", windows.Body.GetProperty("code").GetString());
        var busy = await Post("ReleasePackage", "Android", "http-release");
        Assert.Equal(HttpStatusCode.Conflict, busy.Status); Assert.Equal("PROCESSING_JOB_ACTIVE", busy.Body.GetProperty("code").GetString());
        Assert.Equal(job.ToString(), busy.Body.GetProperty("errors").GetProperty("jobId")[0].GetString());
    }
}
