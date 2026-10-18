using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.API.Controllers;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static readonly object GeometryLease = new { toolchainVersion = "fake-ifc-v1", leaseSeconds = 600 };

    // Geometry pull: the Geometry worker on the VM leases Geometry jobs itself; the dispatcher skips them, the lease is long,
    // expired attempts come back, and the package lease never hands out Geometry (nor the Geometry lease a package job).
    [PostgresFact]
    public async Task Geometry_pull_leases_geometry_jobs_only_with_a_long_lease_and_recovers_expired_attempts()
    {
        var revision = await SeedVerifiedIfcRevision(); var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        await WithProcessingRuntime(async (api, worker) =>
        {
            await using var db = BuildingContext(api);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }).Build();
            var store = new ProcessingRuntimeStore(db, config);
            var geometry = (await store.RequestAsync(adminId, revision, "geometry-pull", default)).Value;

            // With Geometry pull on, the HTTP dispatcher never claims the Geometry event; the package lease ignores it too.
            Assert.Equal("EMPTY", (await store.ExecuteAsync("Claim", "ExcludeGeometry", null, null, default)).GetProperty("code").GetString());
            Assert.Equal("EMPTY", (await store.ExecuteAsync("Claim", "ExcludePackagesAndGeometry", null, null, default)).GetProperty("code").GetString());
            Assert.Null((await store.LeasePackageAsync(WorkerInput(new { toolchainVersion = "fake-unity-v1", leaseSeconds = 300 }), default)).Value);

            var first = (await store.LeaseGeometryAsync(WorkerInput(GeometryLease), default)).Value!.Value;
            Assert.Equal(geometry, first.GetProperty("jobId").GetGuid());
            Assert.Equal("Geometry", first.GetProperty("kind").GetString());
            Assert.Equal(revision, first.GetProperty("revisionId").GetGuid());
            Assert.False(first.GetProperty("replayed").GetBoolean());
            Assert.True(first.GetProperty("pulled").GetBoolean());
            Assert.True(first.GetProperty("leaseUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(500));
            var source = first.GetProperty("sourceInput");
            Assert.Equal("ifc_source", source.GetProperty("artifactType").GetString());
            Assert.Equal(IfcHash, source.GetProperty("sha256").GetString());
            Assert.Equal((long)IfcBytes.Length, source.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(await ScalarAsync($"SELECT storage_url FROM source_documents WHERE revision_id='{revision}'"), source.GetProperty("objectKey").GetString());
            Assert.StartsWith($"worker-output/", first.GetProperty("outputPrefix").GetString());
            Assert.EndsWith($"/{geometry}/{first.GetProperty("attemptId").GetGuid()}/", first.GetProperty("outputPrefix").GetString());
            Assert.Equal("Published", await ScalarAsync($"SELECT status FROM integration_outbox_events WHERE idempotency_key='{first.GetProperty("eventKey").GetString()}'"));
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM integration_event_consumptions WHERE event_key='{first.GetProperty("eventKey").GetString()}'"));
            Assert.Equal("geometry-pull-worker", await ScalarAsync($"SELECT lease_owner FROM processing_job_attempts WHERE id='{first.GetProperty("attemptId").GetGuid()}'"));
            Assert.Null((await store.LeaseGeometryAsync(WorkerInput(GeometryLease), default)).Value);

            // Renew: the pulled attempt takes the configured long lease; without it the core gate keeps 60 s.
            var attempt = first.GetProperty("attemptId").GetGuid(); var token = first.GetProperty("leaseToken").GetGuid();
            var shortRenew = await store.ExecuteAsync("Renew", geometry, WorkerInput(new { attemptId = attempt, leaseToken = token }), default);
            Assert.True(shortRenew.Value.GetProperty("leaseUntil").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddSeconds(120));
            var renewed = await store.ExecuteAsync("Renew", geometry, WorkerInput(new { attemptId = attempt, leaseToken = token, geometryLeaseSeconds = 600 }), default);
            Assert.True(renewed.Value.GetProperty("leaseUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(500));

            // A worker that disappears: once its lease passes, the next lease recovers the job under a new attempt.
            await ExecuteAsync($"UPDATE processing_job_attempts SET lease_until=now()-interval '1 second' WHERE id='{attempt}'");
            var second = (await store.LeaseGeometryAsync(WorkerInput(GeometryLease), default)).Value!.Value;
            Assert.Equal(geometry, second.GetProperty("jobId").GetGuid());
            Assert.NotEqual(attempt, second.GetProperty("attemptId").GetGuid());
            Assert.Equal("Expired", await ScalarAsync($"SELECT status FROM processing_job_attempts WHERE id='{attempt}'"));
            Assert.Equal("ATTEMPT_STALE", (await store.ExecuteAsync("Renew", geometry, WorkerInput(new { attemptId = attempt, leaseToken = token, geometryLeaseSeconds = 600 }), default)).Error?.Code);
            attempt = second.GetProperty("attemptId").GetGuid(); token = second.GetProperty("leaseToken").GetGuid(); var prefix = second.GetProperty("outputPrefix").GetString()!;
            string? hash = null;
            foreach (var type in new[] { "preview_glb", "building_metadata" })
            {
                var output = new { artifactType = type, objectKey = prefix + type, sha256Hash = Sha(type), sizeBytes = 10L, schemaVersion = "1", metadata = new { }, validatorVersion = "fake-v1", outcome = "Passed", issues = Array.Empty<object>() };
                hash = (await store.ExecuteAsync("Output", geometry, WorkerInput(new { attemptId = attempt, leaseToken = token, output }), default)).Value.GetProperty("outputHash").GetString();
            }
            var context = (await store.ContextAsync(attempt, token, default))!.Value;
            Assert.Equal(["building_metadata", "preview_glb"], context.GetProperty("geometryOutputs").EnumerateArray().Select(x => x.GetProperty("artifactType").GetString()!));
            Assert.True((await store.ExecuteAsync("Complete", geometry, WorkerInput(new { attemptId = attempt, leaseToken = token, outputHash = hash }), default)).IsSuccess);
            Assert.Equal("Succeeded", await ScalarAsync($"SELECT status FROM processing_jobs WHERE id='{geometry}'"));

            // A package job of the accepted revision is left to the package lease; ExcludeGeometry alone still delivers it.
            await using var authoringDb = BuildingContext(testConnection); var authoring = new ScenarioWriteStore(authoringDb);
            var scenario = (await authoring.CreateScenarioAsync(adminId, building, null, new(building, "Geometry pull"), default, "geo-pull-scenario")).Value;
            var draft = (await authoring.CreateScenarioDraftAsync(adminId, scenario, null, new(revision), default, "geo-pull-draft")).Value;
            var xmin = Convert.ToUInt32(await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft}'"));
            var updated = await authoring.UpdateScenarioDraftAsync(adminId, draft, xmin, ValidAuthoringState(), null, default);
            var version = (await authoring.SnapshotScenarioDraftAsync(adminId, draft, null, default, "geo-pull-snapshot", updated.Value)).Value;
            var package = (await authoring.BuildAsync(adminId, version, new("PlaytestPackage", "Android"), "geo-pull-build", default)).Value;
            Assert.Null((await store.LeaseGeometryAsync(WorkerInput(GeometryLease), default)).Value);
            Assert.Equal("EMPTY", (await store.ExecuteAsync("Claim", "ExcludePackagesAndGeometry", null, null, default)).GetProperty("code").GetString());
            var delivery = await store.ExecuteAsync("Claim", "ExcludeGeometry", null, null, default);
            Assert.Equal(package, (Guid)(await ScalarAsync($"SELECT aggregate_id FROM integration_outbox_events WHERE idempotency_key='{delivery.GetProperty("eventKey").GetString()}'"))!);
        });
    }

    // Push Geometry attempts keep the 60 s lease even when the API passes geometryLeaseSeconds; the push contract is unchanged.
    [PostgresFact]
    public async Task Pushed_geometry_attempts_keep_the_short_lease()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithProcessingRuntime(async (api, worker) =>
        {
            await using var db = BuildingContext(api);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }).Build();
            var store = new ProcessingRuntimeStore(db, config);
            var job = (await store.RequestAsync(adminId, revision, "geometry-push", default)).Value;
            var delivery = await store.ExecuteAsync("Claim", "ExcludePackages", null, null, default);
            var claim = (await store.ExecuteAsync("Claim", job, WorkerInput(new { eventKey = delivery.GetProperty("eventKey").GetString(), payloadHash = delivery.GetProperty("payloadHash").GetString(), inputHash = IfcHash, toolchainVersion = "fake-ifc-v1", leaseSeconds = 600, geometryLeaseSeconds = 600 }), default)).Value;
            Assert.True(claim.GetProperty("leaseUntil").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddSeconds(120));
            Assert.False(claim.GetProperty("pulled").GetBoolean());
            var renewed = await store.ExecuteAsync("Renew", job, WorkerInput(new { attemptId = claim.GetProperty("attemptId").GetGuid(), leaseToken = claim.GetProperty("leaseToken").GetGuid(), geometryLeaseSeconds = 600 }), default);
            Assert.True(renewed.Value.GetProperty("leaseUntil").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddSeconds(120));
            // The pull lease never takes over a delivery that push already claimed, even once the dispatcher lease has passed
            // (lost ACK): it marks the delivery Published and leaves the attempt to the push worker.
            Assert.Null((await store.LeaseGeometryAsync(WorkerInput(GeometryLease), default)).Value);
            await ExecuteAsync($"UPDATE integration_outbox_events SET lease_until=now()-interval '1 second' WHERE idempotency_key='{delivery.GetProperty("eventKey").GetString()}'");
            Assert.Null((await store.LeaseGeometryAsync(WorkerInput(GeometryLease), default)).Value);
            Assert.Equal("Published", await ScalarAsync($"SELECT status FROM integration_outbox_events WHERE idempotency_key='{delivery.GetProperty("eventKey").GetString()}'"));
            Assert.Equal("http-worker", await ScalarAsync($"SELECT lease_owner FROM processing_job_attempts WHERE id='{claim.GetProperty("attemptId").GetGuid()}'"));
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM processing_job_attempts WHERE processing_job_id='{job}'"));
        });
    }

    private WebApplicationFactory<Program> GeometryWorkerHost(string worker, string key, bool pull, IPackageStorage storage) => factory!.WithWebHostBuilder(web => web
        .ConfigureAppConfiguration((_, b) => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }))
        .ConfigureServices(services =>
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }).Build();
            services.PostConfigure<ProcessingWorkerOptions>(o => { o.WorkerApiEnabled = true; o.MachineKey = key; o.AllowedToolchains = ["fake-ifc-v1"]; o.GeometryPullEnabled = pull; o.GeometryLeaseSeconds = 600; });
            services.RemoveAll<IProcessingWorkerGate>();
            services.AddScoped<IProcessingWorkerGate>(p => new ProcessingRuntimeStore(p.GetRequiredService<Fire3D.Infrastructure.Persistence.Fire3DDbContext>(), config));
            services.RemoveAll<IPackageStorage>();
            services.AddSingleton(storage);
        }));

    [PostgresFact]
    public async Task Geometry_lease_endpoint_needs_the_machine_key_and_pull_mode()
    {
        await WithProcessingRuntime(async (_, worker) =>
        {
            var key = new string('g', 48);
            using (var host = GeometryWorkerHost(worker, key, pull: false, new FakePackageStorage()))
            {
                using var http = host.CreateClient();
                http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
                var body = new { toolchainVersion = "fake-ifc-v1" };
                Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/internal/processing/geometry-jobs/lease", body)).StatusCode);
                http.DefaultRequestHeaders.Authorization = null; http.DefaultRequestHeaders.Add("X-Worker-Key", key);
                var disabled = await http.PostAsJsonAsync("/internal/processing/geometry-jobs/lease", body);
                Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
                Assert.Contains("GEOMETRY_PULL_DISABLED", await disabled.Content.ReadAsStringAsync());
            }
            using (var host = GeometryWorkerHost(worker, key, pull: true, new FakePackageStorage()))
            {
                using var http = host.CreateClient();
                Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/internal/processing/geometry-jobs/lease", new { toolchainVersion = "fake-ifc-v1" })).StatusCode);
                http.DefaultRequestHeaders.Add("X-Worker-Key", key);
                Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/internal/processing/geometry-jobs/lease", new { toolchainVersion = "FAKE-IFC-V1" })).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("/internal/processing/geometry-jobs/lease", new { toolchainVersion = "fake-ifc-v1" })).StatusCode);
            }
        });
    }

    // GET /health/worker checks EXECUTE on the Geometry lease gate only while Geometry pull is on.
    [PostgresFact]
    public async Task Health_worker_checks_the_geometry_lease_gate_when_geometry_pull_is_on()
    {
        var partial = "geo_health_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {partial} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {partial};GRANT EXECUTE ON FUNCTION processing_worker_gate(text,uuid,jsonb) TO {partial}");
        try
        {
            async Task Check(string executor, bool pull, HttpStatusCode expected, string code)
            {
                var key = new string('h', 48);
                using var host = GeometryWorkerHost(executor, key, pull, new FakePackageStorage());
                using var http = host.CreateClient();
                http.DefaultRequestHeaders.Add("X-Worker-Key", key);
                var health = await http.GetAsync("/health/worker");
                Assert.Equal(expected, health.StatusCode);
                Assert.Contains(code, await health.Content.ReadAsStringAsync());
            }
            var login = new Npgsql.NpgsqlConnectionStringBuilder(testConnection) { Username = partial, Pooling = false }.ConnectionString;
            await Check(login, pull: true, HttpStatusCode.ServiceUnavailable, "PROCESSING_EXECUTOR_FORBIDDEN");
            await Check(login + ";Application Name=geometry-off", pull: false, HttpStatusCode.OK, "\"OK\"");
            await WithProcessingRuntime((_, worker) => Check(worker, pull: true, HttpStatusCode.OK, "\"OK\""));
        }
        finally { await ExecuteAsync($"DROP OWNED BY {partial};DROP ROLE {partial}"); }
    }

    // The whole pulled Geometry loop over HTTP: signed source, re-signed inputs, upload URLs, long renew, storage check before Complete.
    [PostgresFact]
    public async Task Geometry_pull_over_http_signs_the_source_and_checks_storage_before_complete()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithProcessingRuntime(async (api, worker) =>
        {
            var key = new string('g', 48); var storage = new FakePackageStorage();
            var sourceKey = (string)(await ScalarAsync($"SELECT storage_url FROM source_documents WHERE revision_id='{revision}'"))!;
            storage.Objects[sourceKey] = IfcBytes;
            await using var db = BuildingContext(api);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }).Build();
            var job = (await new ProcessingRuntimeStore(db, config).RequestAsync(adminId, revision, "geometry-http", default)).Value;
            using var host = GeometryWorkerHost(worker, key, pull: true, storage);
            using var http = host.CreateClient();
            http.DefaultRequestHeaders.Add("X-Worker-Key", key);

            var leased = await http.PostAsJsonAsync("/internal/processing/geometry-jobs/lease", new { toolchainVersion = "fake-ifc-v1" });
            Assert.Equal(HttpStatusCode.OK, leased.StatusCode);
            var lease = await leased.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(["jobId", "kind", "attemptId", "leaseToken", "leaseUntil", "receiptId", "revisionId", "outputPrefix", "inputHash", "replayed", "inputs"],
                lease.EnumerateObject().Select(x => x.Name));
            Assert.Equal(job, lease.GetProperty("jobId").GetGuid());
            Assert.Equal("Geometry", lease.GetProperty("kind").GetString());
            Assert.Equal(revision, lease.GetProperty("revisionId").GetGuid());
            Assert.Equal(IfcHash, lease.GetProperty("inputHash").GetString());
            Assert.True(lease.GetProperty("leaseUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(500));
            var input = Assert.Single(lease.GetProperty("inputs").EnumerateArray());
            Assert.Equal("ifc_source", input.GetProperty("artifactType").GetString());
            Assert.Equal($"https://s3.test/{sourceKey}", input.GetProperty("url").GetString());
            Assert.Equal(IfcHash, input.GetProperty("sha256").GetString());
            Assert.Equal((long)IfcBytes.Length, input.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(await ScalarAsync($"SELECT id FROM source_documents WHERE revision_id='{revision}'"), input.GetProperty("artifactId").GetGuid());
            Assert.DoesNotContain(sourceKey, lease.GetRawText().Replace($"https://s3.test/{sourceKey}", ""));

            var attempt = lease.GetProperty("attemptId").GetGuid(); var token = lease.GetProperty("leaseToken").GetGuid(); var prefix = lease.GetProperty("outputPrefix").GetString()!;
            var inputs = await (await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/inputs", new { attemptId = attempt, leaseToken = token })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal($"https://s3.test/{sourceKey}", Assert.Single(inputs.GetProperty("inputs").EnumerateArray()).GetProperty("url").GetString());
            Assert.Equal(prefix, inputs.GetProperty("outputPrefix").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/inputs", new { attemptId = attempt, leaseToken = Guid.NewGuid() })).StatusCode);

            byte[] glb = "glTF-binary"u8.ToArray(), building = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"fet3d.building/1\"}"), preview = "{}"u8.ToArray();
            string H(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
            var upload = await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/upload-urls", new
            {
                attemptId = attempt, leaseToken = token,
                files = new[] { new { path = "building.glb", sha256 = H(glb), sizeBytes = (long)glb.Length }, new { path = "building_metadata.json", sha256 = H(building), sizeBytes = (long)building.Length }, new { path = "preview_metadata.json", sha256 = H(preview), sizeBytes = (long)preview.Length } }
            });
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var urls = await upload.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(prefix, urls.GetProperty("outputPrefix").GetString());
            Assert.Equal([$"https://s3.test/{prefix}building.glb?put", $"https://s3.test/{prefix}building_metadata.json?put", $"https://s3.test/{prefix}preview_metadata.json?put"],
                urls.GetProperty("files").EnumerateArray().Select(x => x.GetProperty("url").GetString()!));

            var renewed = await (await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/renew", new { attemptId = attempt, leaseToken = token })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(renewed.GetProperty("leaseUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(500));

            async Task<string> Register(string type, string path, byte[] bytes, string schema, object metadata)
            {
                var response = await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/outputs", new
                {
                    attemptId = attempt, leaseToken = token,
                    output = new { artifactType = type, objectKey = prefix + path, sha256Hash = H(bytes), sizeBytes = (long)bytes.Length, schemaVersion = schema, metadata, validatorVersion = "fake-ifc-v1", outcome = "Passed", issues = Array.Empty<object>() }
                });
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outputHash").GetString()!;
            }
            await Register("preview_glb", "building.glb", glb, "fet3d.editor/1", new { });
            var outputHash = await Register("building_metadata", "building_metadata.json", building, "fet3d.building/1", new { schemaVersion = "fet3d.building/1", spaces = Array.Empty<object>() });

            // Nothing uploaded yet: Complete is refused and the attempt stays Running.
            var complete = new { attemptId = attempt, leaseToken = token, outputHash };
            var missing = await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/complete", complete);
            Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
            Assert.Contains("GEOMETRY_STORAGE_MISMATCH", await missing.Content.ReadAsStringAsync());
            storage.Objects[prefix + "building.glb"] = glb; storage.Objects[prefix + "building_metadata.json"] = "{}"u8.ToArray();
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/complete", complete)).StatusCode);
            storage.Objects[prefix + "building_metadata.json"] = building; storage.Fail = true;
            var down = await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/complete", complete);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            Assert.Contains("GEOMETRY_STORAGE_UNAVAILABLE", await down.Content.ReadAsStringAsync());
            Assert.Equal("Running", await ScalarAsync($"SELECT status FROM processing_jobs WHERE id='{job}'"));
            storage.Fail = false;
            var done = await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/complete", complete);
            Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
            Assert.Equal("Succeeded", await ScalarAsync($"SELECT status FROM processing_jobs WHERE id='{job}'"));
            Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM revision_artifacts WHERE job_id='{job}' AND attempt_id='{attempt}'"));
        });
    }
}

public sealed class GeometryWorkerUnitTests
{
    [Fact]
    public void Pull_options_need_the_worker_api_and_http_transport_and_a_bounded_lease()
    {
        var defaults = new ProcessingWorkerOptions();
        Assert.False(defaults.GeometryPullEnabled);
        Assert.Equal(600, defaults.GeometryLeaseSeconds);
        Assert.True(defaults.IsValidPullMode(defaults.GeometryPullEnabled));
        Assert.False(new ProcessingWorkerOptions { GeometryPullEnabled = true }.IsValidPullMode(true));
        Assert.True(new ProcessingWorkerOptions { WorkerApiEnabled = true, GeometryPullEnabled = true }.IsValidPullMode(true));
        Assert.True(new ProcessingWorkerOptions { WorkerApiEnabled = true, DispatcherEnabled = true, Transport = "Http" }.IsValidPullMode(true));
        Assert.False(new ProcessingWorkerOptions { WorkerApiEnabled = true, DispatcherEnabled = true, Transport = "RedisStreams" }.IsValidPullMode(true));
        Assert.False(new ProcessingWorkerOptions { WorkerApiEnabled = true, ConsumerEnabled = true, Transport = "RedisStreams" }.IsValidPullMode(true));
        // RedisStreams configured but neither publisher nor consumer running: nothing pushes, pull is fine.
        Assert.True(new ProcessingWorkerOptions { WorkerApiEnabled = true, Transport = "RedisStreams" }.IsValidPullMode(true));
        foreach (var ok in new[] { 60, 600, 900 }) Assert.True(ProcessingWorkerOptions.IsValidLeaseSeconds(ok));
        foreach (var bad in new[] { 0, 59, 901 }) Assert.False(ProcessingWorkerOptions.IsValidLeaseSeconds(bad));
    }

    [Fact]
    public void Dispatcher_exclusion_follows_the_pull_modes()
    {
        Assert.Null(new ProcessingWorkerOptions().DispatchExclusion);
        Assert.Equal("ExcludePackages", new ProcessingWorkerOptions { PackagePullEnabled = true }.DispatchExclusion);
        Assert.Equal("ExcludeGeometry", new ProcessingWorkerOptions { GeometryPullEnabled = true }.DispatchExclusion);
        Assert.Equal("ExcludePackagesAndGeometry", new ProcessingWorkerOptions { PackagePullEnabled = true, GeometryPullEnabled = true }.DispatchExclusion);
    }

    [Fact]
    public void Startup_validation_rejects_geometry_pull_without_worker_api_or_with_redis_delivery()
    {
        static string? Start(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddOptions<ProcessingWorkerOptions>().Bind(configuration.GetSection("ProcessingWorker"))
                .Validate(o => o.IsValidPullMode(o.GeometryPullEnabled), "Geometry pull requires the worker API and the Http transport.")
                .Validate(o => ProcessingWorkerOptions.IsValidLeaseSeconds(o.GeometryLeaseSeconds), "ProcessingWorker GeometryLeaseSeconds must be 60-900.");
            try { _ = services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Options.IOptions<ProcessingWorkerOptions>>().Value; return null; }
            catch (Microsoft.Extensions.Options.OptionsValidationException ex) { return ex.Message; }
        }
        Assert.Null(Start(new() { ["ProcessingWorker:GeometryPullEnabled"] = "false" }));
        Assert.Null(Start(new() { ["ProcessingWorker:GeometryPullEnabled"] = "true", ["ProcessingWorker:WorkerApiEnabled"] = "true", ["ProcessingWorker:GeometryLeaseSeconds"] = "900" }));
        Assert.Contains("worker API", Start(new() { ["ProcessingWorker:GeometryPullEnabled"] = "true" }));
        Assert.Contains("worker API", Start(new() { ["ProcessingWorker:GeometryPullEnabled"] = "true", ["ProcessingWorker:WorkerApiEnabled"] = "true", ["ProcessingWorker:Transport"] = "RedisStreams", ["ProcessingWorker:DispatcherEnabled"] = "true" }));
        Assert.Contains("GeometryLeaseSeconds", Start(new() { ["ProcessingWorker:GeometryLeaseSeconds"] = "30" }));
    }

    private static JsonElement Context(bool pulled, params (string Key, byte[] Bytes)[] outputs) => JsonSerializer.SerializeToElement(new
    {
        kind = "Geometry", pulled, outputPrefix = "worker-output/o/j/a/",
        geometryOutputs = outputs.Select(o => new { artifactType = o.Key, objectKey = "worker-output/o/j/a/" + o.Key, sha256Hash = Convert.ToHexStringLower(SHA256.HashData(o.Bytes)), sizeBytes = (long)o.Bytes.Length })
    });

    [Fact]
    public async Task Complete_of_a_pulled_geometry_attempt_checks_every_output_in_storage()
    {
        byte[] glb = "glb"u8.ToArray(), meta = "meta"u8.ToArray();
        var storage = new FakePackageStorage();
        var context = Context(true, ("building.glb", glb), ("building_metadata.json", meta));
        Assert.Equal("GEOMETRY_STORAGE_MISMATCH", (await PackageWorkerResponses.VerifyGeometryAsync(context, storage, default))?.Code);
        storage.Objects["worker-output/o/j/a/building.glb"] = glb; storage.Objects["worker-output/o/j/a/building_metadata.json"] = "META"u8.ToArray();
        Assert.Equal("GEOMETRY_STORAGE_MISMATCH", (await PackageWorkerResponses.VerifyGeometryAsync(context, storage, default))?.Code);
        storage.Objects["worker-output/o/j/a/building_metadata.json"] = meta;
        Assert.Null(await PackageWorkerResponses.VerifyGeometryAsync(context, storage, default));
        storage.WithoutChecksum.Add("worker-output/o/j/a/building.glb");
        Assert.Equal("GEOMETRY_STORAGE_MISMATCH", (await PackageWorkerResponses.VerifyGeometryAsync(context, storage, default))?.Code);
        storage.Fail = true;
        Assert.Equal("GEOMETRY_STORAGE_UNAVAILABLE", (await PackageWorkerResponses.VerifyGeometryAsync(context, storage, default))?.Code);
        // Push attempts and package contexts are not checked here.
        Assert.Null(await PackageWorkerResponses.VerifyGeometryAsync(Context(false, ("building.glb", glb)), storage, default));
        Assert.Null(await PackageWorkerResponses.VerifyGeometryAsync(JsonSerializer.SerializeToElement(new { outputPrefix = "x/" }), storage, default));
    }

    [Fact]
    public async Task Source_is_signed_only_for_the_pull_worker_and_internal_fields_never_leave()
    {
        var storage = new FakePackageStorage();
        storage.Objects["ifc/source/o/r/s.ifc"] = new byte[12];
        var claim = JsonSerializer.SerializeToElement(new
        {
            jobId = Guid.NewGuid(), kind = "Geometry", sourceKey = "ifc/source/o/r/s.ifc", pulled = true, geometryOutputs = Array.Empty<object>(),
            sourceInput = new { artifactType = "ifc_source", artifactId = Guid.NewGuid(), objectKey = "ifc/source/o/r/s.ifc", sha256 = new string('a', 64), sizeBytes = 12L }
        });
        var pulled = await PackageWorkerResponses.PresentAsync(claim, storage, default, signSource: true);
        Assert.Equal("https://s3.test/ifc/source/o/r/s.ifc", Assert.Single(pulled.GetProperty("inputs").EnumerateArray()).GetProperty("url").GetString());
        foreach (var hidden in new[] { "sourceKey", "sourceInput", "geometryOutputs", "pulled" }) Assert.False(pulled.TryGetProperty(hidden, out _), hidden);
        var pushed = await PackageWorkerResponses.PresentAsync(claim, storage, default);
        Assert.Equal("ifc/source/o/r/s.ifc", pushed.GetProperty("sourceKey").GetString());
        Assert.False(pushed.TryGetProperty("inputs", out _));
        Assert.False(pushed.TryGetProperty("sourceInput", out _));
        // A source whose size changed is not signed.
        storage.Objects["ifc/source/o/r/s.ifc"] = new byte[13];
        Assert.Equal(JsonValueKind.Null, Assert.Single((await PackageWorkerResponses.PresentAsync(claim, storage, default, signSource: true)).GetProperty("inputs").EnumerateArray()).GetProperty("url").ValueKind);
    }
}
