using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Fire3D.Application.Storage;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Fire3D.AuthTests;

// #219: BE layer 3 of the junk-IFC guard. Limits live in the SQL gates (request_revision_processing, ifc_upload_gate).
public sealed partial class AuthIntegrationTests
{
    // A distinct, valid model per label (different SHA-256) uploaded and adopted in the given building.
    private async Task<Guid> SeedDistinctIfcRevisionIn(Guid building, string label)
    {
        var bytes = Encoding.UTF8.GetBytes(IfcModel("IFC4").Replace("'L1'", $"'{label}'"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var fake = new IfcMemoryStorage(); await using var context = BuildingContext(testConnection);
        var flow = new IfcUploadService(new IfcUploadStore(context), fake.Storage, fake.Inspector, IfcOptions, TimeProvider.System);
        var upload = (await flow.InitiateAsync(adminId, building, new(bytes.Length, "model.ifc", label, hash), "seed-" + label, default)).Value!;
        fake.Put(upload.ObjectKey, bytes);
        var done = await flow.CompleteAsync(adminId, upload.RevisionId, new(upload.ObjectKey, bytes.Length, "application/octet-stream", hash, "model.ifc"), default);
        Assert.True(done.IsSuccess, done.Error?.Code);
        return upload.RevisionId;
    }

    private static IConfiguration ProcessingConfig(string worker, int? dailyLimit = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:ProcessingExecutor"] = worker,
        ["IfcProcessing:DailyLimitPerOrganization"] = dailyLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture)
    }).Build();

    [PostgresFact]
    public async Task Ifc_process_allows_one_queued_or_running_geometry_job_per_organization()
    {
        var building = await SeedIfcBuilding();
        var first = await SeedDistinctIfcRevisionIn(building, "A"); var second = await SeedDistinctIfcRevisionIn(building, "B");
        var otherOrg = await SeedVerifiedIfcRevision();
        await WithProcessingRuntime(async (api, worker) =>
        {
            await using var db = BuildingContext(api);
            var store = new ProcessingRuntimeStore(db, ProcessingConfig(worker));
            var job = await store.RequestAsync(adminId, first, "org-one", default);
            Assert.True(job.IsSuccess, job.Error?.Code);
            var blocked = await store.RequestAsync(adminId, second, "org-two", default);
            Assert.Equal(("IFC_ORG_JOB_RUNNING", 409), (blocked.Error?.Code, blocked.Error?.Status));
            Assert.Equal(new[] { job.Value.ToString() }, blocked.Error!.Errors!["jobId"]);
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM processing_jobs WHERE revision_id='{second}'"));
            Assert.Equal("IFC_ORG_JOB_RUNNING", await ScalarAsync($"SELECT new_values->>'code' FROM audit_logs WHERE target_id='{second}' AND action='Reject'"));
            // A replayed key and the same revision keep answering with the existing job; another Organization is independent.
            Assert.Equal(job.Value, (await store.RequestAsync(adminId, first, "org-one", default)).Value);
            Assert.Equal(job.Value, (await store.RequestAsync(adminId, first, "org-one-again", default)).Value);
            Assert.True((await store.RequestAsync(adminId, otherOrg, "other-org", default)).IsSuccess);
            await ExecuteAsync($"UPDATE processing_jobs SET status='Running' WHERE id='{job.Value}'");
            Assert.Equal("IFC_ORG_JOB_RUNNING", (await store.RequestAsync(adminId, second, "org-two-running", default)).Error?.Code);
            await ExecuteAsync($"UPDATE processing_jobs SET status='Succeeded',finished_at=now() WHERE id='{job.Value}'");
            Assert.True((await store.RequestAsync(adminId, second, "org-two-after", default)).IsSuccess);
        });
    }

    [PostgresFact]
    public async Task Ifc_process_requests_for_two_revisions_of_one_organization_race_to_a_single_job()
    {
        var building = await SeedIfcBuilding();
        var first = await SeedDistinctIfcRevisionIn(building, "R1"); var second = await SeedDistinctIfcRevisionIn(building, "R2");
        await WithProcessingRuntime(async (api, worker) =>
        {
            await using var a = BuildingContext(api); await using var b = BuildingContext(api);
            var results = await Task.WhenAll(new ProcessingRuntimeStore(a, ProcessingConfig(worker)).RequestAsync(adminId, first, "race-1", default),
                new ProcessingRuntimeStore(b, ProcessingConfig(worker)).RequestAsync(adminId, second, "race-2", default));
            Assert.Single(results, x => x.IsSuccess);
            Assert.Single(results, x => x.Error?.Code == "IFC_ORG_JOB_RUNNING");
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM processing_jobs WHERE revision_id IN('{first}','{second}')"));
        });
    }

    [PostgresFact]
    public async Task Ifc_process_daily_limit_counts_new_jobs_per_utc_day_not_retries()
    {
        var building = await SeedIfcBuilding();
        var revisions = new[] { await SeedDistinctIfcRevisionIn(building, "D1"), await SeedDistinctIfcRevisionIn(building, "D2"), await SeedDistinctIfcRevisionIn(building, "D3") };
        await WithProcessingRuntime(async (api, worker) =>
        {
            await using var db = BuildingContext(api);
            var store = new ProcessingRuntimeStore(db, ProcessingConfig(worker, dailyLimit: 2));
            var firstJob = (await store.RequestAsync(adminId, revisions[0], "day-1", default)).Value;
            // A job that failed for a system reason and is requeued keeps its slot: requeue never creates a job.
            await ExecuteAsync($"UPDATE processing_jobs SET status='Failed',finished_at=now() WHERE id='{firstJob}'");
            Assert.Equal("Requeued", await ScalarAsync($"SELECT requeue_processing_job('{firstJob}','retry-day-1','worker crashed')"));
            await ExecuteAsync($"UPDATE processing_jobs SET status='Succeeded',finished_at=now() WHERE id='{firstJob}'");
            Assert.True((await store.RequestAsync(adminId, revisions[1], "day-2", default)).IsSuccess);
            await ExecuteAsync($"UPDATE processing_jobs SET status='Succeeded',finished_at=now() WHERE revision_id='{revisions[1]}'");
            var expected = (int)Math.Ceiling((DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalSeconds);
            var limited = await store.RequestAsync(adminId, revisions[2], "day-3", default);
            Assert.Equal(("IFC_PROCESS_DAILY_LIMIT", 429), (limited.Error?.Code, limited.Error?.Status));
            Assert.InRange(limited.Error!.RetryAfterSeconds!.Value, Math.Max(1, expected - 30), expected + 1);
            Assert.Equal(new[] { "2" }, limited.Error.Errors!["limit"]);
            Assert.Equal(new[] { "2" }, limited.Error.Errors["used"]);
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM processing_jobs WHERE revision_id='{revisions[2]}'"));
            Assert.Equal("IFC_PROCESS_DAILY_LIMIT", await ScalarAsync($"SELECT new_values->>'code' FROM audit_logs WHERE target_id='{revisions[2]}' AND action='Reject'"));
            // The next UTC day: yesterday's jobs no longer count.
            await ExecuteAsync($"UPDATE processing_jobs SET created_at=created_at-interval '1 day' WHERE revision_id IN('{revisions[0]}','{revisions[1]}')");
            Assert.True((await store.RequestAsync(adminId, revisions[2], "day-3-next", default)).IsSuccess);
        });
    }

    [PostgresFact]
    public async Task Ifc_process_http_answers_429_with_retry_after_and_409_with_the_running_job()
    {
        var building = await SeedIfcBuilding();
        var first = await SeedDistinctIfcRevisionIn(building, "H1"); var second = await SeedDistinctIfcRevisionIn(building, "H2");
        using var host = factory!.WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["IfcProcessing:DailyLimitPerOrganization"] = "1" })));
        using var http = host.CreateClient(); http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        async Task<HttpResponseMessage> Process(Guid revision, string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/revisions/{revision}/process");
            request.Headers.Add("Idempotency-Key", key);
            return await http.SendAsync(request);
        }
        var accepted = await Process(first, "http-1");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var job = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();
        var running = await Process(second, "http-2");
        Assert.Equal(HttpStatusCode.Conflict, running.StatusCode);
        using (var problem = JsonDocument.Parse(await running.Content.ReadAsStringAsync()))
        {
            Assert.Equal("IFC_ORG_JOB_RUNNING", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal(job.ToString(), problem.RootElement.GetProperty("errors").GetProperty("jobId")[0].GetString());
        }
        await ExecuteAsync($"UPDATE processing_jobs SET status='Succeeded',finished_at=now() WHERE id='{job}'");
        var limited = await Process(second, "http-3");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta is { TotalSeconds: >= 1 and <= 86400 });
        using var limitedProblem = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("IFC_PROCESS_DAILY_LIMIT", limitedProblem.RootElement.GetProperty("code").GetString());
        using var limits = JsonDocument.Parse(await http.GetStringAsync("/api/ifc/upload-limits"));
        Assert.Equal(1, limits.RootElement.GetProperty("dailyProcessLimitPerOrganization").GetInt32());
    }

    [PostgresFact]
    public async Task Ifc_initiate_refuses_a_source_whose_latest_run_failed_on_blocking_qa_without_a_new_revision()
    {
        var failed = await FinalReviewGeometry("Failed", [new { severity = "Error", code = "IFC_NO_STOREYS", message = "No IfcBuildingStorey." },
            new { severity = "Warning", code = "IFC_UNIT_NOT_METRE", message = "Millimetre model." }]);
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{failed.Revision}'"))!;
        var revisions = await ScalarAsync("SELECT count(*) FROM revisions");
        await WithIfcRuntime(async runtime =>
        {
            await using var context = BuildingContext(runtime);
            var (flow, _) = IfcFlow(context);
            var again = await flow.InitiateAsync(adminId, building, new(IfcBytes.Length, "model.ifc", "again", IfcHash), "same-failed-file", default);
            Assert.Equal(("IFC_SOURCE_ALREADY_FAILED", 409), (again.Error?.Code, again.Error?.Status));
            Assert.Equal(new[] { "IFC_NO_STOREYS" }, again.Error!.Errors!["qaCodes"]);
            Assert.Equal(new[] { failed.Job.ToString() }, again.Error.Errors["jobId"]);
            Assert.Equal(revisions, await ScalarAsync("SELECT count(*) FROM revisions"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM ifc_upload_intents WHERE idempotency_key='same-failed-file'"));
            Assert.Equal("IFC_SOURCE_ALREADY_FAILED", await ScalarAsync($"SELECT new_values->>'code' FROM audit_logs WHERE target_id='{building}' AND action='Reject'"));
            // A fixed file (another SHA-256) is accepted.
            var fixedBytes = Encoding.UTF8.GetBytes(IfcModel("IFC4").Replace("'L1'", "'fixed'"));
            Assert.True((await flow.InitiateAsync(adminId, building, new(fixedBytes.Length, "model.ifc", "fixed", Convert.ToHexStringLower(SHA256.HashData(fixedBytes))), "fixed-file", default)).IsSuccess);
        });
        // Another Organization may still upload the same bytes.
        var other = await SeedIfcBuilding();
        await using var otherContext = BuildingContext(testConnection);
        var (otherFlow, _) = IfcFlow(otherContext);
        Assert.True((await otherFlow.InitiateAsync(adminId, other, new(IfcBytes.Length, "model.ifc", "other", IfcHash), "other-org-file", default)).IsSuccess);
    }

    [PostgresFact]
    public async Task Ifc_initiate_accepts_a_source_whose_latest_run_passed_or_failed_without_blocking_issues()
    {
        var warned = await FinalReviewGeometry("Failed", [new { severity = "Warning", code = "IFC_UNIT_NOT_METRE", message = "Millimetre model." }]);
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{warned.Revision}'"))!;
        await using var context = BuildingContext(testConnection);
        var (flow, _) = IfcFlow(context);
        Assert.True((await flow.InitiateAsync(adminId, building, new(IfcBytes.Length, "model.ifc", "again", IfcHash), "warning-only", default)).IsSuccess);
    }

    [PostgresFact]
    public async Task Ifc_precheck_rejects_a_model_without_storeys_and_creates_no_revision_or_job()
    {
        var building = await SeedIfcBuilding();
        await WithIfcRuntime(async runtime =>
        {
            await using var context = BuildingContext(runtime);
            var (flow, fake) = IfcFlow(context);
            var text = IfcModel("IFC4", "#2=IFCSPACE('0b',$,'101',$,$,$,$,$,.ELEMENT.,.INTERNAL.,$);#3=IFCDOOR('0c',$,'D1',$,$,$,$,$,2.1,0.9,$,$,$);#4=IFCSLAB('0d',$,'S1',$,$,$,$,$,.FLOOR.);");
            var bytes = Encoding.UTF8.GetBytes(text); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var upload = (await flow.InitiateAsync(adminId, building, new(bytes.Length, "model.ifc", "no-storey", hash), "no-storey", default)).Value!;
            fake.Put(upload.ObjectKey, bytes);
            var result = await flow.CompleteAsync(adminId, upload.RevisionId, new FinalizeIfcUploadRequest(upload.ObjectKey, bytes.Length, "application/octet-stream", hash, "model.ifc"), default);
            Assert.Equal(("IFC_PRECHECK_FAILED", 422), (result.Error?.Code, result.Error?.Status));
            Assert.Equal(new[] { "IfcBuildingStorey" }, result.Error!.Errors!["missing"]);
            Assert.Equal(new[] { "IFC_NO_STOREYS" }, result.Error.Errors["qaCodes"]);
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM revisions WHERE id='{upload.RevisionId}'"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM processing_jobs"));
            Assert.Equal(0, fake.Copies);
            Assert.Equal("IFC_NO_STOREYS", await ScalarAsync($"SELECT new_values#>>'{{details,qaCodes,0}}' FROM audit_logs WHERE target_id='{upload.RevisionId}' AND action='Reject'"));
        });
    }
}
