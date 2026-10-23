using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Fire3D.Application.Ifc;
using Fire3D.Application.Releases;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Storage;
using Fire3D.Infrastructure.Ifc;
using Fire3D.Infrastructure.Releases;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.TestHost;
using StackExchange.Redis;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static async Task<HttpResponseMessage> PostKeyed(HttpClient http, string path, object body, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await http.SendAsync(request);
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>())?["code"]?.GetValue<string>();

    private async Task<HttpClient> SignedIn(Guid user, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? host = null)
    {
        if (user != adminId) await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{user}'");
        var email = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{user}'"))!;
        var http = (host ?? factory!).CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync(email)).AccessToken);
        return http;
    }

    // Confirm-for-training, content review and release revoke through HTTP: the controllers map the readiness gate exactly.
    [PostgresFact]
    public async Task Readiness_review_and_release_revoke_HTTP_routes_validate_then_apply_the_gates()
    {
        var f = await SeedReadyPackage();
        using var owner = await SignedIn(f.Owner);
        using var admin = await SignedIn(adminId);

        var confirmPath = $"/api/revisions/{f.Revision}/confirm-for-training";
        var missing = await owner.PostAsJsonAsync(confirmPath, new { scenarioVersionId = f.Version, validationRunId = Guid.Empty });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await Code(missing));
        var confirmed = await owner.PostAsJsonAsync(confirmPath, new { scenarioVersionId = f.Version, validationRunId = f.Run });
        Assert.True(confirmed.StatusCode == HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        var reviewId = (await confirmed.Content.ReadFromJsonAsync<ConfirmationResponse>(Json))!.ReviewId;
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM revision_reviews WHERE id='{reviewId}' AND scenario_version_id='{f.Version}' AND action='ConfirmForTraining'"));

        var submitted = await PostKeyed(owner, $"/api/scenario-versions/{f.Version}/submit", new { }, "http-submit");
        Assert.True(submitted.StatusCode == HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var review = (await submitted.Content.ReadFromJsonAsync<ContentReviewResponse>(Json))!;
        Assert.Equal("Submitted", review.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostKeyed(admin, $"/api/scenario-versions/{f.Version}/submit", new { }, "admin-submit")).StatusCode);

        var approvePath = $"/api/admin/scenario-versions/{f.Version}/approve";
        var badHashes = await PostKeyed(admin, approvePath, new { contentHash = "abc", rubricHash = new string('g', 64) }, "bad-approve");
        Assert.Equal(HttpStatusCode.BadRequest, badHashes.StatusCode);
        var errors = (await badHashes.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(["contentHash", "rubricHash"], errors.Select(x => x.Key).Order());
        var noReason = await PostKeyed(admin, $"/api/admin/scenario-versions/{f.Version}/reject", new { review.ContentHash, review.RubricHash, reason = "  " }, "bad-reject");
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.True((await noReason.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().ContainsKey("reason"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM readiness_command_receipts WHERE idempotency_key IN ('bad-approve','bad-reject')"));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostKeyed(owner, approvePath, new { review.ContentHash, review.RubricHash }, "owner-approve")).StatusCode);

        // Hashes are compared case-insensitively: the controller normalises them before the gate.
        var approved = await PostKeyed(admin, approvePath, new { contentHash = review.ContentHash.ToUpperInvariant(), rubricHash = review.RubricHash.ToUpperInvariant(), reason = " Looks right " }, "http-approve");
        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        Assert.Equal("Approved", (await approved.Content.ReadFromJsonAsync<ContentReviewResponse>(Json))!.Status);
        var late = await PostKeyed(admin, $"/api/admin/scenario-versions/{f.Version}/reject", new { review.ContentHash, review.RubricHash, reason = "Too late" }, "late-reject");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("CONTENT_REVIEW_NOT_PENDING", await Code(late));

        var family = await SeedPlaytestFamily(f.Owner);
        await using (var db = BuildingContext(testConnection))
        {
            var built = await new ReleaseWriteStore(db, TimeProvider.System).BuildAsync(f.Owner, null, new BuildReleaseRequest(f.Revision, f.Version, reviewId, f.Artifact), default, "http-build", family);
            Assert.True(built.IsSuccess, built.Error?.Code);
            var release = built.Value!.Id;
            var blank = await owner.PostAsJsonAsync($"/api/releases/{release}/revoke", new { reason = " " });
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/releases/{Guid.NewGuid()}/revoke", new { reason = "Not ours" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/releases/{release}/revoke", new { reason = "Withdrawn after drill review" })).StatusCode);
            Assert.Equal("Revoked", await ScalarAsync($"SELECT status::text FROM releases WHERE id='{release}'"));
        }

        // A technical rejection for the exact pair is recorded without changing the shared revision status.
        var rejected = await owner.PostAsJsonAsync($"/api/revisions/{f.Revision}/reviews", new { scenarioVersionId = f.Version, validationRunId = f.Run, reviewMessage = "Exit sign misplaced" });
        Assert.True(rejected.StatusCode == HttpStatusCode.Created, await rejected.Content.ReadAsStringAsync());
        var rejection = (await rejected.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        Assert.Equal($"/api/revisions/{f.Revision}/reviews/{rejection}", rejected.Headers.Location!.OriginalString);
        Assert.Equal("ConfirmedForTraining", await ScalarAsync($"SELECT status::text FROM revisions WHERE id='{f.Revision}'"));
        var stale = await owner.PostAsJsonAsync($"/api/revisions/{f.Revision}/reviews", new { scenarioVersionId = f.Version, validationRunId = Guid.NewGuid(), reviewMessage = "Wrong run" });
        Assert.Equal("READINESS_PROVENANCE_MISMATCH", await Code(stale));
    }

    // Read routes of the IFC area that had no HTTP test: revision, processing jobs, BIM fact elements and scenario 404 mapping.
    [PostgresFact]
    public async Task Ifc_and_scenario_read_routes_return_scoped_data_and_problem_codes()
    {
        var revision = await SeedVerifiedIfcRevision();
        var metadata = BuildingMetadataValidatorTests.Fixture();
        metadata["spaces"]!.AsArray().Add(new JsonObject { ["ifcGlobalId"] = "2O2Fr$t4X7Zf8NOew3FLOH", ["category"] = "stair" });
        await SeedEquipmentGeometry(revision, metadata);
        using var admin = await SignedIn(adminId);

        var detail = (await admin.GetFromJsonAsync<JsonObject>($"/api/revisions/{revision}"))!;
        Assert.Equal(revision, detail["id"]!.GetValue<Guid>());
        var unknown = await admin.GetAsync($"/api/revisions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("NOT_FOUND", await Code(unknown));

        var jobs = (await admin.GetFromJsonAsync<JsonObject>($"/api/revisions/{revision}/processing-jobs?page=1&pageSize=5"))!;
        var job = Assert.Single(jobs["items"]!.AsArray())!;
        Assert.Equal(("Geometry", "Succeeded"), (job["kind"]!.GetValue<string>(), job["status"]!.GetValue<string>()));
        Assert.Equal(1, jobs["totalCount"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/revisions/{revision}/processing-jobs?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/revisions/{Guid.NewGuid()}/processing-jobs")).StatusCode);

        var unnamed = (await admin.GetFromJsonAsync<JsonObject>($"/api/revisions/{revision}/bim-facts/elements?qualityFlag=SPACE_UNNAMED"))!;
        var element = Assert.Single(unnamed["items"]!.AsArray())!;
        Assert.Equal("2O2Fr$t4X7Zf8NOew3FLOH", element["ifcGlobalId"]!.GetValue<string>());
        Assert.Contains("SPACE_UNNAMED", element["qualityFlags"]!.AsArray().Select(x => x!.GetValue<string>()));
        var all = (await admin.GetFromJsonAsync<JsonObject>($"/api/revisions/{revision}/bim-facts/elements?entityType=IfcSpace&pageSize=100"))!;
        Assert.True(all["items"]!.AsArray().Count > 1);
        var badFilter = await admin.GetAsync($"/api/revisions/{revision}/bim-facts/elements?ifcGlobalId=short");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
        Assert.True((await badFilter.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().ContainsKey("ifcGlobalId"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/revisions/{Guid.NewGuid()}/bim-facts/elements")).StatusCode);

        foreach (var path in new[] { $"/api/scenario-versions/{Guid.NewGuid()}", $"/api/scenarios/{Guid.NewGuid()}/versions", $"/api/scenarios/{Guid.NewGuid()}/drafts", $"/api/scenario-drafts/{Guid.NewGuid()}" })
        {
            var response = await admin.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, path);
            Assert.Equal("NOT_FOUND", await Code(response));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/scenarios/{Guid.NewGuid()}/drafts?pageSize=0")).StatusCode);
    }

    // The whole playtest runtime surface over HTTP from one session that prepared it (no Mobile handoff).
    [PostgresFact]
    public async Task Playtest_HTTP_runtime_routes_download_launch_sync_complete_and_list()
    {
        var f = await SeedPlaytestRuntime();
        var storage = new FakePackageStorage();
        using var host = factory!.WithWebHostBuilder(web =>
        {
            web.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Playtest:Enabled"] = "true", ["Playtest:SigningKey"] = HandoffOptions.SigningKey,
                ["Playtest:HandoffKey"] = HandoffOptions.HandoffKey, ["Playtest:HandoffDeepLinkBaseUrl"] = HandoffOptions.HandoffDeepLinkBaseUrl
            }));
            web.ConfigureTestServices(services => { services.RemoveAll<IPackageStorage>(); services.AddSingleton<IPackageStorage>(storage); });
        });
        using var http = await SignedIn(f.Owner, host);

        var availability = (await http.GetFromJsonAsync<PlaytestAvailability>($"/api/scenarios/{f.Scenario}/playtest-availability?scenarioVersionId={f.Version}", Json))!;
        Assert.True(availability.CanCreate, string.Join(",", availability.Blockers));
        Assert.Equal(((bool?)true, "Trial", (int?)1), (availability.PackageReady, availability.Entitlement.Kind, availability.Entitlement.TrialUnitsRemaining));

        var prepared = await PostKeyed(http, $"/api/scenarios/{f.Scenario}/playtests", new { revisionId = f.Revision, scenarioVersionId = f.Version }, "cov-prepare");
        Assert.True(prepared.StatusCode == HttpStatusCode.Created, await prepared.Content.ReadAsStringAsync());
        var id = (await prepared.Content.ReadFromJsonAsync<PlaytestPreparation>(Json))!.Id;
        var path = $"/api/playtests/{id}";

        var oldFormat = await http.GetAsync(path + "/package");
        Assert.Equal(HttpStatusCode.Conflict, oldFormat.StatusCode);
        Assert.Equal("PACKAGE_FORMAT_UNSUPPORTED", await Code(oldFormat));
        var metadata = JsonSerializer.Serialize(PackageMetadata());
        await ExecuteAsync($"UPDATE revision_artifacts SET schema_version='fet3d.package/1',object_key='worker-output/o/j/a/manifest.json',metadata=metadata||'{metadata}'::jsonb WHERE id=(SELECT manifest_artifact_id FROM playtest_package_pins WHERE playtest_id='{id}')");
        var package = (await http.GetFromJsonAsync<Fire3D.API.Controllers.RuntimePackageDownload>(path + "/package", Json))!;
        Assert.Equal("https://s3.test/worker-output/o/j/a/manifest.json", package.Manifest.Url);
        Assert.Equal(["https://s3.test/worker-output/o/j/a/floor_L1", "https://s3.test/worker-output/o/j/a/floor_L1_lighting"], package.Files.Select(x => x.Url));
        Assert.Equal(("Android", PackageManifest.SchemaVersion), (package.BuildTarget, package.SchemaVersion));

        Assert.Equal(HttpStatusCode.OK, (await PostKeyed(http, path + "/start", new { runtimeVersion = "1.0.0" }, "cov-start")).StatusCode);
        var reissued = await PostKeyed(http, path + "/launch-grants", new { }, "cov-reissue");
        Assert.True(reissued.StatusCode == HttpStatusCode.OK, await reissued.Content.ReadAsStringAsync());
        Assert.Equal(2, (await reissued.Content.ReadFromJsonAsync<PlaytestLaunch>(Json))!.Generation);
        var stale = await http.PostAsJsonAsync(path + "/launched", new { generation = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("PLAYTEST_GRANT_STALE", await Code(stale));
        Assert.Equal("Running", (await (await http.PostAsJsonAsync(path + "/launched", new { generation = 2 })).Content.ReadFromJsonAsync<PlaytestStatusResponse>(Json))!.Status);
        Assert.Equal("Running", (await (await http.PostAsync(path + "/heartbeat", null)).Content.ReadFromJsonAsync<PlaytestHeartbeatResponse>(Json))!.Status);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.PostAsJsonAsync(path + "/events:batch", new { events = Array.Empty<object>() })).StatusCode);
        var events = await http.PostAsJsonAsync(path + "/events:batch", new PlaytestEventsRequest([Event(1), Event(2)]));
        Assert.True(events.StatusCode == HttpStatusCode.OK, await events.Content.ReadAsStringAsync());
        Assert.Equal(2, (await events.Content.ReadFromJsonAsync<PlaytestEventsResponse>(Json))!.AcknowledgedSequence);

        var negative = await PostKeyed(http, path + "/complete", new { lastEventSequence = -1 }, "cov-bad-complete");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);
        var completed = await PostKeyed(http, path + "/complete", new { lastEventSequence = 2, summary = new { reachedExit = true } }, "cov-complete");
        Assert.True(completed.StatusCode == HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());
        Assert.Equal("Completed", (await completed.Content.ReadFromJsonAsync<PlaytestStatusResponse>(Json))!.Status);
        var cancel = await PostKeyed(http, path + "/cancel", new { }, "cov-cancel");
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal("PLAYTEST_TERMINAL", await Code(cancel));

        var invalidCode = await http.PostAsJsonAsync("/api/playtests/handoffs/redeem", new { code = "not-a-handoff-code" });
        Assert.Equal(HttpStatusCode.NotFound, invalidCode.StatusCode);
        Assert.Equal("PLAYTEST_HANDOFF_INVALID", await Code(invalidCode));

        var page = (await http.GetFromJsonAsync<PlaytestPage>($"/api/scenarios/{f.Scenario}/playtests?status=Completed&scenarioVersionId={f.Version}", Json))!;
        Assert.Equal(id, Assert.Single(page.Items).Id);
        Assert.Empty((await http.GetFromJsonAsync<PlaytestPage>($"/api/scenarios/{f.Scenario}/playtests?status=Running", Json))!.Items);
        // The only Trial unit is used: the advisory check now reports the entitlement blocker.
        var after = (await http.GetFromJsonAsync<PlaytestAvailability>($"/api/scenarios/{f.Scenario}/playtest-availability", Json))!;
        Assert.False(after.CanCreate);
        Assert.Contains("PLAYTEST_ENTITLEMENT_REQUIRED", after.Blockers);
        Assert.Equal(1, await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{f.Trial}'"));
    }

    // Request-shape guards of IFC command controllers and the worker key check answer before any gate runs.
    [PostgresFact]
    public async Task Malformed_preconditions_package_kinds_and_wrong_worker_keys_are_refused_before_any_gate()
    {
        using var admin = await SignedIn(adminId);
        var revision = Guid.NewGuid();
        foreach (var (method, path, body) in new[]
        {
            (HttpMethod.Patch, $"/api/revisions/{revision}/equipment-decisions", (object)new { items = new[] { new { candidateId = "door-1", decision = "Confirmed" } } }),
            (HttpMethod.Put, $"/api/revisions/{revision}/style", new { presetVersionId = Guid.NewGuid() })
        })
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("If-Match", "W/\"3\"");
            var response = await admin.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, path + " " + response.StatusCode);
            Assert.Equal("VALIDATION_ERROR", await Code(response));
        }
        var build = await PostKeyed(admin, $"/api/scenario-versions/{Guid.NewGuid()}/package-builds", new { kind = "DesktopPackage", buildTarget = "Windows" }, "bad-kind");
        Assert.Equal(HttpStatusCode.BadRequest, build.StatusCode);
        Assert.True((await build.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().ContainsKey("kind"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM processing_jobs"));

        var key = new string('k', 48);
        using var host = factory!.WithWebHostBuilder(web => web
            .ConfigureAppConfiguration((_, b) => b.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ProcessingExecutor"] = testConnection }))
            .ConfigureServices(services => services.PostConfigure<ProcessingWorkerOptions>(o => { o.WorkerApiEnabled = true; o.MachineKey = key; o.AllowedToolchains = ["fake-v1"]; })));
        using var worker = host.CreateClient();
        worker.DefaultRequestHeaders.Add("X-Worker-Key", new string('x', 48));
        var wrong = await worker.PostAsJsonAsync($"/internal/processing/jobs/{Guid.NewGuid()}/fail", new { attemptId = Guid.NewGuid(), leaseToken = Guid.NewGuid(), reason = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    // A source already gone from S3 counts as cleaned; shutdown during delete leaves the lease to expire and retry.
    [PostgresFact]
    public async Task Ifc_cleanup_treats_a_missing_object_as_cleaned_and_shutdown_keeps_the_claim()
    {
        await ExecuteAsync("INSERT INTO ifc_object_cleanup(object_key,available_at) VALUES('ifc/orphans/missing.ifc',now()-interval '1 second')");
        using var stop = new CancellationTokenSource();
        var deleted = new List<string>();
        var storage = ResetProxy.For<IStorageService>((method, args) => method == nameof(IStorageService.DeleteObjectAsync)
            ? Delete((string)args[0]!, (CancellationToken)args[1]!) : throw new Exception("Unexpected: " + method));
        async Task Delete(string key, CancellationToken ct)
        {
            deleted.Add(key);
            if (key.EndsWith("missing.ifc")) throw new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound };
            await stop.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
        }
        var services = new ServiceCollection();
        services.AddScoped(_ => BuildingContext(testConnection)); services.AddScoped(_ => storage);
        using var provider = services.BuildServiceProvider();
        var worker = new IfcUploadCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(), IfcOptions, NullLogger<IfcUploadCleanupWorker>.Instance);

        Assert.True(await worker.RunOnceAsync(default));
        Assert.Equal(true, await ScalarAsync("SELECT cleaned_at IS NOT NULL AND last_error IS NULL AND lease_token IS NULL FROM ifc_object_cleanup WHERE object_key='ifc/orphans/missing.ifc'"));

        await ExecuteAsync("INSERT INTO ifc_object_cleanup(object_key,available_at) VALUES('ifc/orphans/slow.ifc',now()-interval '1 second')");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(stop.Token));
        Assert.Equal(["ifc/orphans/missing.ifc", "ifc/orphans/slow.ifc"], deleted);
        Assert.Equal(true, await ScalarAsync("SELECT cleaned_at IS NULL AND lease_token IS NOT NULL AND attempts=1 FROM ifc_object_cleanup WHERE object_key='ifc/orphans/slow.ifc'"));
    }

    // Real Redis + PostgreSQL: a failed worker call is recorded and deferred, the reclaimed message then confirms one receipt.
    [RedisPostgresFact]
    public async Task Redis_consumer_defers_a_failed_handoff_then_confirms_the_worker_receipt_once()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var db = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var runtime = new ProcessingRuntimeStore(db, config);
            var job = (await runtime.RequestAsync(adminId, revision, "redis-confirm", default)).Value;
            var options = RedisStreamIntegrationTests.TestOptions();
            options.PendingIdleSeconds = 0;
            await using var stream = new RedisProcessingStream(Options.Create(options), RedisStreamIntegrationTests.TestEnvironment("test-" + Guid.NewGuid().ToString("N")));
            await using var connection = await RedisStreamIntegrationTests.ConnectAsync(options);
            using var scopes = RedisScope(config);
            try
            {
                Assert.True(await new RedisProcessingPublisher(scopes.GetRequiredService<IServiceScopeFactory>(), stream, Options.Create(new ProcessingWorkerOptions()), NullLogger<RedisProcessingPublisher>.Instance).RunOnceAsync(default));
                var calls = 0;
                var handler = new FakeProcessingHttp(async request =>
                {
                    if (Interlocked.Increment(ref calls) == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    var envelope = (await request.Content!.ReadFromJsonAsync<ProcessingEnvelope>())!;
                    var claim = await runtime.ExecuteAsync("Claim", job, WorkerInput(new { eventKey = envelope.EventKey, payloadHash = envelope.PayloadHash, inputHash = IfcHash, toolchainVersion = "fake-v1" }), default);
                    Assert.True(claim.IsSuccess, claim.Error?.Code);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { eventKey = envelope.EventKey, payloadHash = envelope.PayloadHash, receiptId = claim.Value.GetProperty("receiptId").GetGuid() }) };
                });
                var consumer = new RedisProcessingConsumer(scopes.GetRequiredService<IServiceScopeFactory>(), stream, ResetProxy.For<IHttpClientFactory>((_, _) => new HttpClient(handler, false)),
                    Options.Create(new ProcessingWorkerOptions { WorkerUrl = "https://worker.example.test/deliver", MachineKey = new string('k', 32) }), NullLogger<RedisProcessingConsumer>.Instance);
                async Task<long> Pending() => (await connection.GetDatabase().StreamPendingAsync(stream.StreamName, options.Group)).PendingMessageCount;

                // Read, fail with 503, record the failure; the immediate reclaim in the same run is DEFERRED, not delivered again.
                await consumer.RunOnceAsync(default);
                Assert.Equal(1, calls);
                Assert.Equal(1L, await Pending());
                Assert.Equal(1, await ScalarAsync("SELECT delivery_failures FROM processing_stream_state"));
                Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM integration_event_consumptions"));

                await ExecuteAsync("UPDATE processing_stream_state SET next_delivery_at=now()-interval '1 second'");
                await consumer.RunOnceAsync(default);
                Assert.Equal(2, calls);
                Assert.Equal(0L, await Pending());
                Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM integration_event_consumptions WHERE consumer_name='processing-bridge-v1'"));
                Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM processing_job_attempts WHERE processing_job_id='{job}'"));
                Assert.Equal("Running", await ScalarAsync($"SELECT status::text FROM processing_jobs WHERE id='{job}'"));
            }
            finally { await connection.GetDatabase().KeyDeleteAsync(stream.StreamName); }
        });
    }

    // Real Redis + PostgreSQL: recovery deletes an old entry only after PostgreSQL confirms its handoff.
    [RedisPostgresFact]
    public async Task Redis_recovery_trims_an_old_confirmed_entry_and_keeps_an_unreadable_one()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var db = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var runtime = new ProcessingRuntimeStore(db, config);
            var job = (await runtime.RequestAsync(adminId, revision, "redis-trim", default)).Value;
            var options = RedisStreamIntegrationTests.TestOptions();
            await using var stream = new RedisProcessingStream(Options.Create(options), RedisStreamIntegrationTests.TestEnvironment("test-" + Guid.NewGuid().ToString("N")));
            await using var connection = await RedisStreamIntegrationTests.ConnectAsync(options);
            var redis = connection.GetDatabase();
            using var scopes = RedisScope(config);
            try
            {
                var dispatch = (IProcessingStreamDispatchGate)new ProcessingStreamStore(config);
                var claim = await dispatch.ExecuteAsync("Claim", WorkerInput(new { streamName = stream.StreamName }), default);
                var envelope = claim.GetProperty("envelope");
                var old = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds();
                // An entry written eight days ago (beyond RetentionDays) and an unreadable neighbour.
                await redis.StreamAddAsync(stream.StreamName, [new NameValueEntry("envelope", envelope.GetRawText())], $"{old}-0");
                await redis.StreamAddAsync(stream.StreamName, [new NameValueEntry("envelope", "{broken")], $"{old}-1");
                Assert.Equal("OK", (await dispatch.ExecuteAsync("Published", WorkerInput(new { eventKey = envelope.GetProperty("eventKey").GetString(), leaseToken = claim.GetProperty("leaseToken").GetGuid(), streamName = stream.StreamName, streamId = $"{old}-0" }), default)).GetProperty("code").GetString());
                var read = await stream.ReadAsync("worker", default);
                Assert.Equal(2, read.Count);
                var log = new ListLogger<RedisProcessingRecovery>();
                var recovery = new RedisProcessingRecovery(scopes.GetRequiredService<IServiceScopeFactory>(), stream,
                    Options.Create(new ProcessingWorkerOptions { Transport = "RedisStreams", ConsumerEnabled = true }), Options.Create(options), log);

                // Delivered but not yet handed off: nothing is deleted.
                foreach (var message in read) await stream.AcknowledgeAsync(message.Id, default);
                await recovery.RunOnceAsync(default);
                Assert.Equal(2, await redis.StreamLengthAsync(stream.StreamName));

                Assert.True((await runtime.ExecuteAsync("Claim", job, WorkerInput(new { eventKey = envelope.GetProperty("eventKey").GetString(), payloadHash = envelope.GetProperty("payloadHash").GetString(), inputHash = IfcHash, toolchainVersion = "fake-v1" }), default)).IsSuccess);
                await recovery.RunOnceAsync(default);
                var remaining = Assert.Single(await redis.StreamRangeAsync(stream.StreamName));
                Assert.Equal($"{old}-1", remaining.Id.ToString());
                Assert.Contains(log.Messages, m => m.Text == "Processing Redis recovery: Replayed=0, Trimmed=1, StreamLength=1, Pending=0");
            }
            finally { await redis.KeyDeleteAsync(stream.StreamName); }
        });
    }
}
