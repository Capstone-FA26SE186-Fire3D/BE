using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Fire3D.API.Controllers;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Fire3D.Infrastructure.Scenarios;
using Fire3D.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static object PackageMetadata() => new
    {
        buildTarget = "Android", protocolVersion = "1", manifestSchemaVersion = "1", minRuntimeVersion = "2.0.0", runtimeVersion = "2.0.0",
        requiredCapabilities = new[] { "door.exit" }, unityVersion = "6000.6.3f1", materialLibraryVersion = "1",
        floors = new[] { new { id = "L1", bundle = "floor_L1", lightingBundle = "floor_L1_lighting", scene = "L1" } },
        files = new[] { new { path = "floor_L1", sha256 = Sha("floor"), size = 5L }, new { path = "floor_L1_lighting", sha256 = Sha("light"), size = 5L } }
    };

    // BE#110: the Unity VM leases package jobs itself; the dispatcher skips them, the lease is long, expired attempts come back.
    [PostgresFact]
    public async Task Package_pull_leases_package_jobs_only_with_a_long_lease_and_recovers_expired_attempts()
    {
        var revision = await SeedVerifiedIfcRevision(); var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        await using var authoringDb = BuildingContext(testConnection); var authoring = new ScenarioWriteStore(authoringDb);
        var scenario = (await authoring.CreateScenarioAsync(adminId, building, null, new(building, "Pull"), default, "pull-scenario")).Value;
        var draft = (await authoring.CreateScenarioDraftAsync(adminId, scenario, null, new(revision), default, "pull-draft")).Value;
        var xmin = Convert.ToUInt32(await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft}'"));
        var updated = await authoring.UpdateScenarioDraftAsync(adminId, draft, xmin, ValidAuthoringState(), null, default);
        var version = (await authoring.SnapshotScenarioDraftAsync(adminId, draft, null, default, "pull-snapshot", updated.Value)).Value;
        await WithProcessingRuntime(async (api, worker) =>
        {
            await using var db = BuildingContext(api);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }).Build();
            var store = new ProcessingRuntimeStore(db, config);

            // With pull on, the HTTP dispatcher delivers the Geometry job and never the package job.
            var geometry = (await store.RequestAsync(adminId, revision, "pull-geometry", default)).Value;
            var delivery = await store.ExecuteAsync("Claim", "ExcludePackages", null, null, default);
            Assert.Equal(geometry, (Guid)(await ScalarAsync($"SELECT aggregate_id FROM integration_outbox_events WHERE idempotency_key='{delivery.GetProperty("eventKey").GetString()}'"))!);
            Assert.Equal("EMPTY", (await store.ExecuteAsync("Claim", "ExcludePackages", null, null, default)).GetProperty("code").GetString());
            var g = (await store.ExecuteAsync("Claim", geometry, WorkerInput(new { eventKey = delivery.GetProperty("eventKey").GetString(), payloadHash = delivery.GetProperty("payloadHash").GetString(), inputHash = IfcHash, toolchainVersion = "fake-v1" }), default)).Value;
            string? geometryHash = null;
            foreach (var type in new[] { "preview_glb", "building_metadata" })
            {
                var output = new { artifactType = type, objectKey = g.GetProperty("outputPrefix").GetString() + type, sha256Hash = Sha(type), sizeBytes = 10L, schemaVersion = "1", metadata = new { }, validatorVersion = "fake-v1", outcome = "Passed", issues = Array.Empty<object>() };
                geometryHash = (await store.ExecuteAsync("Output", geometry, WorkerInput(new { attemptId = g.GetProperty("attemptId").GetGuid(), leaseToken = g.GetProperty("leaseToken").GetGuid(), output }), default)).Value.GetProperty("outputHash").GetString();
            }
            Assert.True((await store.ExecuteAsync("Complete", geometry, WorkerInput(new { attemptId = g.GetProperty("attemptId").GetGuid(), leaseToken = g.GetProperty("leaseToken").GetGuid(), outputHash = geometryHash }), default)).IsSuccess);

            // One live job per revision: the package build starts once Geometry is accepted.
            var job = (await authoring.BuildAsync(adminId, version, new("PlaytestPackage", "Android"), "pull-build", default)).Value;
            var request = new { toolchainVersion = "fake-unity-v1", buildTarget = "Android", leaseSeconds = 300 };
            Assert.Null((await store.LeasePackageAsync(WorkerInput(request with { buildTarget = "Windows" }), default)).Value);
            var first = (await store.LeasePackageAsync(WorkerInput(request), default)).Value!.Value;
            Assert.Equal(job, first.GetProperty("jobId").GetGuid());
            Assert.True(first.GetProperty("leaseUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(200));
            Assert.Equal(["building_metadata", "preview_glb"], first.GetProperty("geometryInputs").EnumerateArray().Select(x => x.GetProperty("artifactType").GetString()));
            Assert.Equal("Published", await ScalarAsync($"SELECT status FROM integration_outbox_events WHERE idempotency_key='{first.GetProperty("eventKey").GetString()}'"));
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM integration_event_consumptions WHERE event_key='{first.GetProperty("eventKey").GetString()}'"));
            Assert.Null((await store.LeasePackageAsync(WorkerInput(request), default)).Value);

            var attempt = first.GetProperty("attemptId").GetGuid(); var token = first.GetProperty("leaseToken").GetGuid(); var prefix = first.GetProperty("outputPrefix").GetString()!;
            object Output(string type, string key) => new { attemptId = attempt, leaseToken = token, output = new { artifactType = type, objectKey = key, sha256Hash = Sha("manifest"), sizeBytes = 99L, schemaVersion = PackageManifest.SchemaVersion, metadata = PackageMetadata(), validatorVersion = "fake-unity-v1", outcome = "Passed", issues = Array.Empty<object>() } };
            Assert.Equal("PACKAGE_MANIFEST_INVALID", (await store.ExecuteAsync("Output", job, WorkerInput(Output("manifest", prefix + "nested/manifest.json")), default)).Error?.Code);
            Assert.True((await store.ExecuteAsync("Output", job, WorkerInput(Output("manifest", prefix + "manifest.json")), default)).IsSuccess);
            var hash = (await store.ExecuteAsync("Output", job, WorkerInput(Output("unity_package", prefix + "floor_L1")), default)).Value.GetProperty("outputHash").GetString();
            // The package is the manifest: unity_package must name the same object.
            Assert.Equal("PACKAGE_MANIFEST_INVALID", (await store.ExecuteAsync("Complete", job, WorkerInput(new { attemptId = attempt, leaseToken = token, outputHash = hash }), default)).Error?.Code);
            var renewed = await store.ExecuteAsync("Renew", job, WorkerInput(new { attemptId = attempt, leaseToken = token, leaseSeconds = 300 }), default);
            Assert.True(renewed.Value.GetProperty("leaseUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(200));

            // A worker that disappears: once its lease passes, the next lease recovers the job under a new attempt.
            await ExecuteAsync($"UPDATE processing_job_attempts SET lease_until=now()-interval '1 second' WHERE id='{attempt}'");
            var second = (await store.LeasePackageAsync(WorkerInput(request), default)).Value!.Value;
            Assert.Equal(job, second.GetProperty("jobId").GetGuid());
            Assert.NotEqual(attempt, second.GetProperty("attemptId").GetGuid());
            Assert.Equal("ATTEMPT_STALE", (await store.ExecuteAsync("Renew", job, WorkerInput(new { attemptId = attempt, leaseToken = token, leaseSeconds = 300 }), default)).Error?.Code);
            attempt = second.GetProperty("attemptId").GetGuid(); token = second.GetProperty("leaseToken").GetGuid(); prefix = second.GetProperty("outputPrefix").GetString()!;
            Assert.True((await store.ExecuteAsync("Output", job, WorkerInput(Output("manifest", prefix + "manifest.json")), default)).IsSuccess);
            hash = (await store.ExecuteAsync("Output", job, WorkerInput(Output("unity_package", prefix + "manifest.json")), default)).Value.GetProperty("outputHash").GetString();
            Assert.True((await store.ExecuteAsync("Complete", job, WorkerInput(new { attemptId = attempt, leaseToken = token, outputHash = hash }), default)).IsSuccess);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(DISTINCT object_key) FROM revision_artifacts WHERE job_id='{job}' AND attempt_id='{attempt}' AND is_runtime_ready"));
        });
    }

    [PostgresFact]
    public async Task Package_lease_endpoint_needs_the_machine_key_and_pull_mode()
    {
        await WithProcessingRuntime(async (_, worker) =>
        {
            var key = new string('k', 48); var pull = false;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }).Build();
            using var host = factory!.WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, b) => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker })).ConfigureServices(services =>
            {
                services.PostConfigure<ProcessingWorkerOptions>(o => { o.WorkerApiEnabled = true; o.MachineKey = key; o.AllowedToolchains = ["fake-unity-v1"]; o.PackagePullEnabled = pull; });
                Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IProcessingWorkerGate>(services);
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IProcessingWorkerGate>(services, p => new ProcessingRuntimeStore(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Fire3D.Infrastructure.Persistence.Fire3DDbContext>(p), config));
                Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IPackageStorage>(services);
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IPackageStorage>(services, new FakePackageStorage());
            }));
            using var http = host.CreateClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
            var body = new { toolchainVersion = "fake-unity-v1", buildTarget = "Android" };
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, "/internal/processing/package-jobs/lease", body)).StatusCode);
            http.DefaultRequestHeaders.Authorization = null; http.DefaultRequestHeaders.Add("X-Worker-Key", key);
            var disabled = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, "/internal/processing/package-jobs/lease", body);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, disabled.StatusCode);
            Assert.Contains("PACKAGE_PULL_DISABLED", await disabled.Content.ReadAsStringAsync());
        });
        await WithProcessingRuntime(async (_, worker) =>
        {
            var key = new string('k', 48);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker }).Build();
            using var host = factory!.WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, b) => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = worker })).ConfigureServices(services =>
            {
                services.PostConfigure<ProcessingWorkerOptions>(o => { o.WorkerApiEnabled = true; o.MachineKey = key; o.AllowedToolchains = ["fake-unity-v1"]; o.PackagePullEnabled = true; });
                Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IProcessingWorkerGate>(services);
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IProcessingWorkerGate>(services, p => new ProcessingRuntimeStore(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Fire3D.Infrastructure.Persistence.Fire3DDbContext>(p), config));
            }));
            using var http = host.CreateClient();
            http.DefaultRequestHeaders.Add("X-Worker-Key", key);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, "/internal/processing/package-jobs/lease", new { toolchainVersion = "unknown" })).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, "/internal/processing/package-jobs/lease", new { toolchainVersion = "fake-unity-v1" })).StatusCode);
            var stale = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, $"/internal/processing/jobs/{Guid.NewGuid()}/upload-urls",
                new { attemptId = Guid.NewGuid(), leaseToken = Guid.NewGuid(), files = new[] { new { path = "floor_L1", sha256 = new string('a', 64), sizeBytes = 5 } } });
            Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
            var unsafePath = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, $"/internal/processing/jobs/{Guid.NewGuid()}/upload-urls",
                new { attemptId = Guid.NewGuid(), leaseToken = Guid.NewGuid(), files = new[] { new { path = "../other-job/x", sha256 = new string('a', 64), sizeBytes = 5 } } });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, unsafePath.StatusCode);
        });
    }

    // FR-TRAINING-04: prepare pins the package and Mobile downloads/verifies it before start; start alone uses the seat.
    [PostgresFact]
    public async Task Learner_package_files_are_downloadable_after_prepare_without_using_a_seat()
    {
        var f = await PublishedTraining();
        var trainee = await SeedTrainee(); var other = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new Fire3D.Infrastructure.Learning.LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new Fire3D.Application.Learning.LearnerCaller(trainee.Id, trainee.Family, null);
            var prepared = await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "pkg-learn-prepare", default);
            Assert.True(prepared.IsSuccess, prepared.Error?.Code);
            var id = prepared.Value!.Id;
            var metadata = JsonSerializer.Serialize(PackageMetadata());
            await ExecuteAsync($"UPDATE revision_artifacts SET schema_version='fet3d.package/1',object_key='worker-output/o/j/l/manifest.json',metadata=metadata||'{metadata}'::jsonb WHERE id=(SELECT (package->>'manifestArtifactId')::uuid FROM training_session_preparations WHERE id='{id}')");
            await using var reader = BuildingContext(testConnection);
            var files = new RuntimePackageFilesStore(reader);
            var before = await files.GetAsync("Training", trainee.Id, trainee.Family, id, default);
            Assert.True(before.IsSuccess, before.Error?.Code);
            Assert.Equal("worker-output/o/j/l/", before.Value.GetProperty("prefix").GetString());
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM billing_learner_seats WHERE trainee_id='{trainee.Id}'"));
            Assert.Equal("NOT_FOUND", (await files.GetAsync("Training", other.Id, other.Family, id, default)).Error?.Code);

            Assert.True((await store.StartAsync(caller, id, "pkg-learn-start", default)).IsSuccess);
            Assert.True((await files.GetAsync("Training", trainee.Id, trainee.Family, id, default)).IsSuccess);

            // An expired preparation no longer downloads.
            var second = (await store.PrepareAsync(caller, new(f.Training, "Guided", "1.0.0"), "pkg-learn-prepare-2", default)).Value!.Id;
            await ExecuteAsync($"UPDATE training_session_preparations SET expires_at=now()-interval '1 second' WHERE id='{second}'");
            Assert.Equal("PACKAGE_SESSION_NOT_ACTIVE", (await files.GetAsync("Training", trainee.Id, trainee.Family, second, default)).Error?.Code);
        });
    }

    // Mobile#22 / FR-TRAINING-04: the Mobile login that redeemed the handoff downloads the pinned fet3d.package/1 files before start
    // (start then rechecks entitlement), and keeps them while the playtest runs.
    [PostgresFact]
    public async Task Playtest_package_files_are_offered_only_to_the_mobile_session_before_and_after_start()
    {
        var f = await SeedPlaytestRuntime();
        var stranger = await SeedReadyPackage("PlaytestPackage"); var strangerFamily = await SeedPlaytestFamily(stranger.Owner);
        var login = "package_files_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION playtest_lifecycle_gate(text,uuid,uuid,uuid,uuid,jsonb,text),playtest_read_gate(text,uuid,uuid,uuid,jsonb),playtest_runtime_gate(text,uuid,uuid,uuid,jsonb,text),runtime_package_files(text,uuid,uuid,uuid) TO {login}");
        try
        {
            var runtime = new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString;
            await using var db = BuildingContext(runtime);
            var store = new PlaytestLifecycle(db, Options.Create(HandoffOptions));
            var id = (await store.PrepareAsync(f.Owner, f.Web, f.Building, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "pkg-prepare", default)).Value!.Id;
            var files = new RuntimePackageFilesStore(db);
            Assert.Equal("PACKAGE_SESSION_NOT_ACTIVE", (await files.GetAsync("Playtest", f.Owner, f.Web, id, default)).Error?.Code);
            var code = (await store.CreateHandoffAsync(f.Owner, f.Web, id, "pkg-handoff", default)).Value!.Code;
            Assert.True((await store.RedeemHandoffAsync(f.Owner, f.Mobile, new(code), default)).IsSuccess);
            // A package built before fet3d.package/1 has no file list.
            Assert.Equal("PACKAGE_FORMAT_UNSUPPORTED", (await files.GetAsync("Playtest", f.Owner, f.Mobile, id, default)).Error?.Code);

            var metadata = JsonSerializer.Serialize(PackageMetadata());
            await ExecuteAsync($"UPDATE revision_artifacts SET schema_version='fet3d.package/1',object_key='worker-output/o/j/a/manifest.json',metadata=metadata||'{metadata}'::jsonb WHERE id=(SELECT manifest_artifact_id FROM playtest_package_pins WHERE playtest_id='{id}')");
            // Before start: the redeeming Mobile session downloads and verifies; the web session that prepared it does not.
            Assert.True((await files.GetAsync("Playtest", f.Owner, f.Mobile, id, default)).IsSuccess);
            Assert.Equal("PACKAGE_SESSION_NOT_ACTIVE", (await files.GetAsync("Playtest", f.Owner, f.Web, id, default)).Error?.Code);
            Assert.Equal(0, await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{f.Trial}'"));
            Assert.True((await store.StartAsync(f.Owner, f.Mobile, id, new("1.0.0"), "pkg-start", default)).IsSuccess);
            var pinned = await files.GetAsync("Playtest", f.Owner, f.Mobile, id, default);
            Assert.True(pinned.IsSuccess, pinned.Error?.Code);
            Assert.Equal("worker-output/o/j/a/", pinned.Value.GetProperty("prefix").GetString());
            Assert.Equal("PACKAGE_SESSION_NOT_ACTIVE", (await files.GetAsync("Playtest", f.Owner, f.Web, id, default)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await files.GetAsync("Playtest", stranger.Owner, strangerFamily, id, default)).Error?.Code);
            Assert.Equal("FORBIDDEN", (await files.GetAsync("Training", f.Owner, f.Mobile, id, default)).Error?.Code);

            var download = await PackageWorkerResponses.DownloadAsync(pinned.Value, new FakePackageStorage(), default);
            Assert.Equal(["https://s3.test/worker-output/o/j/a/floor_L1", "https://s3.test/worker-output/o/j/a/floor_L1_lighting"], download.Files.Select(x => x.Url));
            Assert.Equal("https://s3.test/worker-output/o/j/a/manifest.json", download.Manifest.Url);
            Assert.Equal(Sha("floor"), download.Files[0].Sha256);
        }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }
}

public sealed class PackageWorkerUnitTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static (FakePackageStorage Storage, JsonElement Context) Package(Action<FakePackageStorage, string>? tamper = null)
    {
        var storage = new FakePackageStorage();
        var prefix = "worker-output/o/j/a/";
        byte[] floor = "floor"u8.ToArray(), light = "light"u8.ToArray();
        storage.Objects[prefix + "floor_L1"] = floor; storage.Objects[prefix + "floor_L1_lighting"] = light;
        var metadata = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["path"] = "floor_L1", ["sha256"] = Sha(floor), ["size"] = floor.Length }, new JsonObject { ["path"] = "floor_L1_lighting", ["sha256"] = Sha(light), ["size"] = light.Length }),
            ["floors"] = new JsonArray(new JsonObject { ["bundle"] = "floor_L1", ["lightingBundle"] = "floor_L1_lighting" })
        };
        var manifest = Encoding.UTF8.GetBytes(new JsonObject { ["schemaVersion"] = "fet3d.package/1", ["files"] = metadata["files"]!.DeepClone(), ["floors"] = metadata["floors"]!.DeepClone() }.ToJsonString());
        storage.Objects[prefix + "manifest.json"] = manifest;
        tamper?.Invoke(storage, prefix);
        var context = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["outputPrefix"] = prefix,
            ["packageManifest"] = new JsonObject { ["schemaVersion"] = "fet3d.package/1", ["objectKey"] = prefix + "manifest.json", ["sha256Hash"] = Sha(manifest), ["sizeBytes"] = manifest.Length, ["metadata"] = metadata }
        });
        return (storage, context);
    }

    [Fact]
    public async Task Complete_checks_every_uploaded_file_against_the_manifest()
    {
        var (ok, context) = Package();
        Assert.Null(await PackageWorkerResponses.VerifyAsync(context, ok, default));
        foreach (var tamper in new Action<FakePackageStorage, string>[]
        {
            (s, p) => s.Objects[p + "floor_L1"] = "FLOOR"u8.ToArray(),          // same size, other bytes
            (s, p) => s.Objects.Remove(p + "floor_L1_lighting"),               // not uploaded
            (s, p) => s.Objects[p + "manifest.json"] = "{}"u8.ToArray(),       // manifest differs from the registered output
            (s, p) => s.WithoutChecksum.Add(p + "floor_L1")                    // storage cannot prove the SHA-256
        })
        {
            var (storage, tampered) = Package(tamper);
            Assert.Equal("PACKAGE_STORAGE_MISMATCH", (await PackageWorkerResponses.VerifyAsync(tampered, storage, default))?.Code);
        }
        var (failing, again) = Package();
        failing.Fail = true;
        Assert.Equal("PACKAGE_STORAGE_UNAVAILABLE", (await PackageWorkerResponses.VerifyAsync(again, failing, default))?.Code);
    }

    [Fact]
    public async Task Upload_urls_sign_the_sha256_checksum_header()
    {
        using var client = new AmazonS3Client(new BasicAWSCredentials("AKIDEXAMPLE", "fixture-secret"), RegionEndpoint.APSoutheast1);
        var storage = new S3PackageStorage(client, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AWS:BucketName"] = "fet3d-test" }).Build());
        var sha = Sha("floor"u8.ToArray());
        var upload = await storage.SignPutAsync("worker-output/o/j/a/floor_L1", sha, TimeSpan.FromMinutes(30), default);
        Assert.Contains("x-amz-checksum-sha256", Uri.UnescapeDataString(upload.Url));
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString(sha)), upload.Headers["x-amz-checksum-sha256"]);
        Assert.InRange(upload.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
    }

    [Fact]
    public void Manifest_metadata_lists_unique_safe_files_and_floor_bundles()
    {
        var valid = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            buildTarget = "Android", protocolVersion = "1", manifestSchemaVersion = "1", minRuntimeVersion = "2.0.0", runtimeVersion = "2.0.0",
            requiredCapabilities = Array.Empty<string>(), unityVersion = "6000.6.3f1", materialLibraryVersion = "1",
            floors = new[] { new { bundle = "floor_L1" } }, files = new[] { new { path = "floor_L1", sha256 = new string('a', 64), size = 1L } }
        }))!;
        Assert.Empty(PackageManifest.Validate(valid, "Android"));
        Assert.Contains(PackageManifest.Validate(valid, "Windows"), x => x.Path == "metadata.buildTarget");
        var bad = valid.DeepClone();
        bad["files"]![0]!["path"] = "../escape"; bad["floors"]![0]!["bundle"] = "missing";
        var paths = PackageManifest.Validate(bad, null).Select(x => x.Path).ToList();
        Assert.Contains("metadata.files[0].path", paths);
        Assert.Contains("metadata.floors[0].bundle", paths);
        Assert.False(PackageManifest.IsValidPath("a/./b"));
        Assert.False(PackageManifest.IsValidPath("/abs"));
    }
}

/// <summary>In-memory IPackageStorage: URLs are https://s3.test/{key}; HEAD reports size and SHA-256 like S3 with ChecksumMode.</summary>
internal sealed class FakePackageStorage : IPackageStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public HashSet<string> WithoutChecksum { get; } = new(StringComparer.Ordinal);
    public bool Fail { get; set; }

    public Task<SignedDownload?> SignVerifiedGetAsync(string key, long sizeBytes, TimeSpan lifetime, CancellationToken ct) =>
        Task.FromResult(Objects.TryGetValue(key, out var bytes) && bytes.Length == sizeBytes ? new SignedDownload($"https://s3.test/{key}", DateTimeOffset.UtcNow.Add(lifetime)) : null);
    public Task<SignedDownload> SignGetAsync(string key, TimeSpan lifetime, CancellationToken ct) => Task.FromResult(new SignedDownload($"https://s3.test/{key}", DateTimeOffset.UtcNow.Add(lifetime)));
    public Task<SignedUpload> SignPutAsync(string key, string sha256Hex, TimeSpan lifetime, CancellationToken ct) =>
        Task.FromResult(new SignedUpload($"https://s3.test/{key}?put", new Dictionary<string, string> { ["x-amz-checksum-sha256"] = sha256Hex }, DateTimeOffset.UtcNow.Add(lifetime)));
    public Task<(long Size, string? Sha256Hex)?> HeadAsync(string key, CancellationToken ct)
    {
        if (Fail) throw new HttpRequestException("storage down");
        return Task.FromResult<(long, string?)?>(Objects.TryGetValue(key, out var bytes) ? (bytes.Length, WithoutChecksum.Contains(key) ? null : Convert.ToHexStringLower(SHA256.HashData(bytes))) : null);
    }
    public Task<byte[]?> ReadAsync(string key, long maxBytes, CancellationToken ct) => Task.FromResult(Objects.TryGetValue(key, out var bytes) && bytes.Length <= maxBytes ? bytes : null);
}
