using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Administration;
using Fire3D.Application.Releases;
using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Scenarios;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Readiness_reads_report_missing_steps_before_submission_and_stay_tenant_scoped()
    {
        var f = await SeedReadyPackage(); var other = await SeedReadyPackage();
        var login = "authoring_read_" + Guid.NewGuid().ToString("N");
        // A runtime identity with only EXECUTE on the gate: the SECURITY DEFINER owner reads the tables.
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION authoring_readiness_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO {login}");
        try
        {
            await using var db = BuildingContext(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString);
            var queries = new ScenarioReviewQueries(db);
            var before = (await queries.VersionReadinessAsync(f.Owner, f.Owner, f.Version, default)).Value!;
            Assert.Equal("NotSubmitted", before.ReviewStatus); Assert.Equal("Passed", before.Outcome); Assert.Equal(f.Run, before.ValidationRunId);
            Assert.NotNull(before.PackageJob); Assert.False(before.TechnicalReady); Assert.Equal(["CONFIRMATION_REQUIRED"], before.Missing);

            // Confirm-for-training needs a runtime-ready artifact, so readiness names that blocker before any confirmation exists.
            await ExecuteAsync($"UPDATE revision_artifacts SET is_runtime_ready=false WHERE id='{f.Artifact}'");
            var noArtifact = (await queries.VersionReadinessAsync(f.Owner, f.Owner, f.Version, default)).Value!;
            Assert.Equal(["RUNTIME_ARTIFACT_REQUIRED", "CONFIRMATION_REQUIRED"], noArtifact.Missing);
            await ExecuteAsync($"UPDATE revision_artifacts SET is_runtime_ready=true WHERE id='{f.Artifact}'");

            await ApproveRelease(f);
            var after = (await queries.VersionReadinessAsync(f.Owner, f.Owner, f.Version, default)).Value!;
            Assert.True(after.TechnicalReady); Assert.Empty(after.Missing); Assert.Equal("Approved", after.ReviewStatus);
            Assert.Equal(f.Artifact, after.CandidateArtifactId); Assert.NotNull(after.ConfirmationReviewId);

            await ExecuteAsync($"INSERT INTO revision_issues(id,validation_run_id,severity,code,message,details,created_at) VALUES(gen_random_uuid(),'{f.Run}','Critical','fixture-blocker','Fixture blocker','{{}}',now())");
            var blocked = (await queries.VersionReadinessAsync(f.Owner, f.Owner, f.Version, default)).Value!;
            Assert.False(blocked.TechnicalReady); Assert.Contains("QA_BLOCKERS_PRESENT", blocked.Missing);
            Assert.Equal("fixture-blocker", Assert.Single(blocked.Blockers).IssueCode);

            var revision = (await queries.RevisionReadinessAsync(f.Owner, f.Owner, f.Revision, default)).Value!;
            Assert.Equal(f.Revision, revision.RevisionId);
            Assert.Equal(f.Version, Assert.Single(revision.Confirmations).ScenarioVersionId);

            Assert.Equal("NOT_FOUND", (await queries.VersionReadinessAsync(other.Owner, other.Owner, f.Version, default)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await queries.RevisionReadinessAsync(other.Owner, other.Owner, f.Revision, default)).Error?.Code);
            Assert.True((await queries.VersionReadinessAsync(adminId, adminId, f.Version, default)).IsSuccess);
            await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{f.Owner}'");
            Assert.Equal("UNAUTHORIZED", (await queries.VersionReadinessAsync(f.Owner, f.Owner, f.Version, default)).Error?.Code);
        }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }

    [PostgresFact]
    public async Task Work_resumes_from_building_and_scenario_drafts_releases_and_trainings_over_HTTP()
    {
        var f = await SeedReadyPackage(); var request = await ApproveRelease(f);
        var scenario = (Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;
        await ExecuteAsync("INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities) VALUES(gen_random_uuid(),'1.0.0','1','1','[]') ON CONFLICT DO NOTHING");
        await using (var db = BuildingContext(testConnection))
        {
            var built = await new Fire3D.Infrastructure.Releases.ReleaseWriteStore(db, TimeProvider.System).BuildAsync(f.Owner, null, request, default, "resume-build", f.Owner);
            Assert.True(built.IsSuccess, built.Error?.Code);
        }
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM scenario_versions WHERE id='{f.Version}'"))!;
        await SeedPublishPaidEntitlement(f.Owner, building);
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{f.Owner}'");
        var email = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{f.Owner}'"))!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync(email)).AccessToken);

        // Drafts of the scenario, newest save first, with the ETag revision and last editor.
        var drafts = await client.GetFromJsonAsync<PageResponse<ScenarioDraftSummaryResponse>>($"/api/scenarios/{scenario}/drafts", Json);
        var draft = Assert.Single(drafts!.Items);
        Assert.Equal(f.Owner, draft.LastEditedById); Assert.Equal("Owner", draft.LastEditedByName); Assert.True(draft.Version > 0);
        var detail = await client.GetFromJsonAsync<ScenarioDetailResponse>($"/api/scenarios/{scenario}", Json);
        Assert.Equal(draft.Id, detail!.CurrentDraftId); Assert.Equal(draft.Version, detail.CurrentDraftVersion);
        Assert.Equal(1, detail.DraftCount); Assert.Equal(f.Version, detail.LatestVersionId);
        var reopened = await client.GetAsync($"/api/scenario-drafts/{draft.Id}");
        Assert.Equal($"\"{draft.Version}\"", reopened.Headers.ETag!.ToString());

        // Releases found from the Building or the scenario, with the service period on the detail.
        var releases = await client.GetFromJsonAsync<PageResponse<ReleaseSummaryResponse>>($"/api/releases?buildingId={building}&status=Built", Json);
        var release = Assert.Single(releases!.Items);
        Assert.Equal(scenario, release.ScenarioId); Assert.Equal(f.Version, release.ScenarioVersionId);
        Assert.Equal(1, (await client.GetFromJsonAsync<PageResponse<ReleaseSummaryResponse>>($"/api/releases?scenarioId={scenario}", Json))!.TotalCount);
        Assert.Equal(0, (await client.GetFromJsonAsync<PageResponse<ReleaseSummaryResponse>>($"/api/releases?buildingId={building}&status=Published", Json))!.TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/releases?status=Live")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/releases?status=7")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/releases?status=Built,Revoked")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/releases?status=built")).StatusCode);
        var full = await client.GetFromJsonAsync<ReleaseResponse>($"/api/releases/{release.Id}", Json);
        Assert.True(full!.Entitlement!.Paid); Assert.Equal("Active", full.Entitlement.Status);

        // Trainings carry the release state, version and review status.
        var trainings = JsonDocument.Parse(await client.GetStringAsync($"/api/buildings/{building}/trainings")).RootElement;
        var training = Assert.Single(trainings.EnumerateArray());
        Assert.Equal("Built", training.GetProperty("releaseStatus").GetString());
        Assert.Equal(f.Version, training.GetProperty("scenarioVersionId").GetGuid());
        Assert.Equal("Approved", training.GetProperty("reviewStatus").GetString());

        // Readiness over HTTP for the version and the revision.
        Assert.True((await client.GetFromJsonAsync<ScenarioVersionReadiness>($"/api/scenario-versions/{f.Version}/readiness", Json))!.TechnicalReady);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/revisions/{f.Revision}/readiness")).StatusCode);

        // Another tenant sees none of it.
        var other = await SeedReadyPackage();
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{other.Owner}'");
        using var stranger = factory!.CreateClient();
        stranger.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync((string)(await ScalarAsync($"SELECT email FROM users WHERE id='{other.Owner}'"))!)).AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/scenarios/{scenario}/drafts")).StatusCode);
        Assert.Equal(0, (await stranger.GetFromJsonAsync<PageResponse<ReleaseSummaryResponse>>($"/api/releases?buildingId={building}", Json))!.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/scenario-versions/{f.Version}/readiness")).StatusCode);
    }
}
