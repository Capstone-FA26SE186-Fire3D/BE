using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Learning;
using Fire3D.Infrastructure.Releases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static readonly LearnerSessionOptions LearnerOptions = new()
    {
        Enabled = true, SigningKey = "fixture-only-learner-launch-key-32-bytes!!",
        ContinuationSigningKey = "fixture-only-learner-continuation-key-32b!", ContinuationTtlDays = 7
    };
    private static readonly JsonObject ScorableRubric = JsonNode.Parse("""
        {"schema_version":"1","pass_threshold":0.5,"criteria":[
          {"id":"exit","metric":"reached_exit","mandatory":true,"weight":1,"operator":"eq","threshold":1},
          {"id":"fast","metric":"completion_time_seconds","mandatory":false,"weight":1,"operator":"lte","threshold":600}]}
        """)!.AsObject();

    private sealed record LearnerFixture(Guid Owner, Guid OwnerFamily, Guid Building, Guid Training, Guid Entitlement);

    private async Task<LearnerFixture> PublishedTraining(JsonObject? rubric = null, bool publicBuilding = true)
    {
        var f = await SeedReadyPackage(rubric: rubric);
        var input = await ApproveRelease(f);
        var family = await SeedPlaytestFamily(f.Owner);
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        await SeedPublishPaidEntitlement(f.Owner, building, order: Random.Shared.NextInt64(100000, 900000));
        Guid release = default;
        await WithReleaseRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var built = await new ReleaseWriteStore(db, TimeProvider.System).BuildAsync(f.Owner, null, input, default, "learner-build", family);
            Assert.True(built.IsSuccess, built.Error?.Code);
            release = built.Value!.Id;
            using var published = JsonDocument.Parse((string)(await ScalarAsync($"SELECT publish_release_gate('Publish','{f.Owner}','{family}','{release}','{{}}','learner-publish',NULL)::text"))!);
            Assert.Equal("Published", published.RootElement.GetProperty("result").GetProperty("status").GetString());
        });
        if (publicBuilding) await ExecuteAsync($"UPDATE buildings SET visibility='Public' WHERE id='{building}'");
        var training = (Guid)(await ScalarAsync($"SELECT id FROM trainings WHERE release_id='{release}'"))!;
        var entitlement = (Guid)(await ScalarAsync($"SELECT id FROM service_entitlements WHERE building_id='{building}' AND status='Active'"))!;
        return new(f.Owner, family, building, training, entitlement);
    }

    private async Task<(Guid Id, Guid Family)> SeedTrainee()
    {
        var id = Guid.NewGuid(); var family = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO users(id,email,username,role,is_active,email_verified_at,created_at,updated_at,password_hash) VALUES('{id}','trainee-{id:N}@example.test','t{id.ToString("N")[..20]}','Trainee',true,now(),now(),now(),(SELECT password_hash FROM users WHERE id='{adminId}'));
            INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{id}','{family}','{Guid.NewGuid():N}{Guid.NewGuid():N}',now(),now()+interval '1 hour');
            """);
        return (id, family);
    }

    private async Task WithLearnerRuntime(Func<string, Task> action)
    {
        var login = "learner_runtime_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION learner_session_gate(text,uuid,uuid,uuid,uuid,jsonb,text),building_qr_gate(text,uuid,uuid,uuid,jsonb) TO {login}");
        try { await action(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString); }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }

    private static TrainingEvent LearnerEvent(long sequence, string type, long elapsedMs, object? payload = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), sequence, "1", type, DateTimeOffset.Parse("2026-10-11T00:00:00Z").AddMilliseconds(elapsedMs), elapsedMs, JsonSerializer.SerializeToElement(payload ?? new { }));

    private static async Task<Guid> Running(LearnerSessions store, LearnerCaller caller, Guid training, string mode, string key)
    {
        var prepared = await store.PrepareAsync(caller, new(training, mode, "1.0.0"), key + "-prepare", default);
        Assert.True(prepared.IsSuccess, prepared.Error?.Code);
        var started = await store.StartAsync(caller, prepared.Value!.Id, key + "-start", default);
        Assert.True(started.IsSuccess, started.Error?.Code);
        Assert.True((await store.LaunchedAsync(caller, prepared.Value.Id, new(started.Value!.Launch.Generation), default)).IsSuccess);
        return prepared.Value.Id;
    }

    [PostgresFact]
    public async Task Learner_prepare_start_seat_sync_and_server_rubric_result()
    {
        var f = await PublishedTraining(ScorableRubric);
        var trainee = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var prepared = await store.PrepareAsync(caller, new(f.Training, "Assessment", "1.0.0"), "prepare", default);
            Assert.True(prepared.IsSuccess, prepared.Error?.Code);
            Assert.Equal("Prepared", prepared.Value!.Status);
            Assert.Equal(prepared.Value.Id, (await store.PrepareAsync(caller, new(f.Training, "Assessment", "1.0.0"), "prepare", default)).Value!.Id);
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", (await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "prepare", default)).Error?.Code);
            // Prepare is not a play and holds no seat.
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM billing_learner_seats"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM sessions WHERE contract_version=7"));
            var id = prepared.Value.Id;

            var started = await store.StartAsync(caller, id, "start", default);
            Assert.True(started.IsSuccess, started.Error?.Code);
            Assert.Equal("Launching", started.Value!.Session.Status);
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM billing_learner_seats"));
            var principal = new JwtSecurityTokenHandler().ValidateToken(started.Value.Launch.LaunchGrant, new TokenValidationParameters { ValidIssuer = LearnerOptions.Issuer, ValidAudience = LearnerOptions.Audience, ClockSkew = TimeSpan.Zero, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LearnerOptions.SigningKey)) }, out _);
            Assert.Equal("learner_launch", principal.FindFirstValue("purpose")); Assert.Equal(id.ToString(), principal.FindFirstValue("session_id"));
            var continuation = new JwtSecurityTokenHandler().ValidateToken(started.Value.Continuation.Token, new TokenValidationParameters { ValidIssuer = LearnerOptions.ContinuationIssuer, ValidAudience = LearnerOptions.ContinuationAudience, ClockSkew = TimeSpan.Zero, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LearnerOptions.ContinuationSigningKey)) }, out _);
            Assert.Equal("learner_continuation", continuation.FindFirstValue("purpose"));
            Assert.InRange((started.Value.Continuation.ExpiresAt - DateTime.UtcNow).TotalDays, 6.9, 7.1);
            Assert.Equal(started.Value.Launch.LaunchGrant, (await store.StartAsync(caller, id, "start", default)).Value!.Launch.LaunchGrant);
            // Before launch the same session may obtain a newer grant; it is still one play and one seat.
            var regrant = await store.StartAsync(caller, id, "start-again", default);
            Assert.Equal(2, regrant.Value!.Launch.Generation);
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM sessions WHERE contract_version=7"));
            Assert.Equal("LAUNCH_GRANT_STALE", (await store.LaunchedAsync(caller, id, new(1), default)).Error?.Code);
            Assert.Equal("Running", (await store.LaunchedAsync(caller, id, new(2), default)).Value!.Status);
            Assert.Equal("SESSION_ALREADY_LAUNCHED", (await store.StartAsync(caller, id, "start-late", default)).Error?.Code);

            Assert.Equal("VALIDATION_ERROR", (await store.RecordEventsAsync(caller, id, new([LearnerEvent(1, "HazardExposure", 10, new { amount = -1 })]), default)).Error?.Code);
            var moved = LearnerEvent(1, "Moved", 1000, new { distanceMeters = 5.5 });
            var batch = (await store.RecordEventsAsync(caller, id, new([moved, LearnerEvent(3, "ExitReached", 120000)]), default)).Value!;
            Assert.Equal(1, batch.AcknowledgedSequence); Assert.Equal([new TrainingSequenceRange(2, 2)], batch.MissingRanges);
            Assert.Equal([moved.EventId], (await store.RecordEventsAsync(caller, id, new([moved]), default)).Value!.Duplicates);
            Assert.Equal("EVENT_HASH_CONFLICT", Assert.Single((await store.RecordEventsAsync(caller, id, new([moved with { ElapsedMs = 9 }]), default)).Value!.Conflicts).Code);

            var awaiting = await store.CompleteAsync(caller, id, new(3, "Finished"), "complete", default);
            Assert.Equal(202, awaiting.Value.Status); Assert.Equal("AwaitingSync", awaiting.Value.Body.Status);
            Assert.Equal("RESULT_NOT_READY", (await store.ResultAsync(caller, id, default)).Error?.Code);
            Assert.Equal("COMPLETION_ALREADY_REQUESTED", (await store.CompleteAsync(caller, id, new(4, "Finished"), "complete-2", default)).Error?.Code);
            Assert.Equal("EVENT_AFTER_COMPLETION", Assert.Single((await store.RecordEventsAsync(caller, id, new([LearnerEvent(4, "Moved", 130000, new { distanceMeters = 1 })]), default)).Value!.Conflicts).Code);
            var filled = (await store.RecordEventsAsync(caller, id, new([LearnerEvent(2, "HazardExposure", 60000, new { amount = 2.25 })]), default)).Value!;
            Assert.Equal("Completed", filled.Status); Assert.Equal(3, filled.AcknowledgedSequence);

            var result = (await store.ResultAsync(caller, id, default)).Value!;
            Assert.Equal("Passed", result.Outcome); Assert.Equal(100m, result.ScorePercent); Assert.Equal(2, result.Criteria!.Count);
            Assert.Equal(1, result.Metrics.GetProperty("reached_exit").GetInt32()); Assert.Equal(120, result.Metrics.GetProperty("completion_time_seconds").GetDecimal());
            Assert.Equal(5.5m, result.Metrics.GetProperty("distance_meters").GetDecimal()); Assert.Equal(2.25m, result.Metrics.GetProperty("hazard_exposure").GetDecimal());
            var replay = await store.CompleteAsync(caller, id, new(3, "Finished"), "complete", default);
            Assert.Equal(200, replay.Value.Status);
            Assert.Equal("SESSION_TERMINAL", (await store.HeartbeatAsync(caller, id, default)).Error?.Code);
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE session_results SET outcome='NotPassed' WHERE session_id='{id}'"));
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM session_events WHERE session_id='{id}'"));

            // A second play of the same Trainee in the same period reuses the seat; Abandoned Assessment is Incomplete, Learn is NotAssessed.
            var second = await Running(store, caller, f.Training, "Assessment", "second");
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM billing_learner_seats"));
            Assert.Equal(200, (await store.CompleteAsync(caller, second, new(0, "Abandoned"), "second-complete", default)).Value.Status);
            Assert.Equal("Incomplete", (await store.ResultAsync(caller, second, default)).Value!.Outcome);
            var learn = await Running(store, caller, f.Training, "Learn", "learn");
            await store.RecordEventsAsync(caller, learn, new([LearnerEvent(1, "ExitReached", 5000)]), default);
            await store.CompleteAsync(caller, learn, new(1, "Finished"), "learn-complete", default);
            var learnResult = (await store.ResultAsync(caller, learn, default)).Value!;
            Assert.Equal("NotAssessed", learnResult.Outcome); Assert.Null(learnResult.ScorePercent); Assert.Null(learnResult.Criteria);
            Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM sessions WHERE contract_version=7"));
        });
    }

    [PostgresFact]
    public async Task Learner_last_seat_race_admits_one_and_upgrade_or_renewal_changes_capacity()
    {
        var f = await PublishedTraining();
        await ExecuteAsync($"ALTER TABLE service_entitlements DISABLE TRIGGER USER; UPDATE service_entitlements SET commercial_version=7,learner_limit=1 WHERE id='{f.Entitlement}'; ALTER TABLE service_entitlements ENABLE TRIGGER USER");
        var a = await SeedTrainee(); var b = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var dbA = BuildingContext(runtime); await using var dbB = BuildingContext(runtime);
            var storeA = new LearnerSessions(dbA, Options.Create(LearnerOptions)); var storeB = new LearnerSessions(dbB, Options.Create(LearnerOptions));
            var callerA = new LearnerCaller(a.Id, a.Family, null); var callerB = new LearnerCaller(b.Id, b.Family, null);
            var prepA = (await storeA.PrepareAsync(callerA, new(f.Training, "Learn", "1.0.0"), "a", default)).Value!.Id;
            var prepB = (await storeB.PrepareAsync(callerB, new(f.Training, "Learn", "1.0.0"), "b", default)).Value!.Id;
            var race = await Task.WhenAll(storeA.StartAsync(callerA, prepA, "a-start", default), storeB.StartAsync(callerB, prepB, "b-start", default));
            Assert.Single(race, x => x.IsSuccess);
            Assert.Equal("LEARNER_LIMIT_REACHED", Assert.Single(race, x => !x.IsSuccess).Error!.Code);
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM billing_learner_seats"));
            var (loser, loserCaller, loserPrep) = race[0].IsSuccess ? (storeB, callerB, prepB) : (storeA, callerA, prepA);
            // Upgrade raises the limit on the same period; consumed seats are kept.
            await ExecuteAsync($"""
                INSERT INTO billing_entitlement_upgrades(entitlement_id,capacity_revision,previous_learner_limit,learner_limit,additional_quota_units,effective_from,quotation_item_id,payment_transaction_id,provisioning_key)
                SELECT id,1,1,2,0,now()-interval '1 minute',quotation_item_id,payment_transaction_id,'learner-upgrade' FROM service_entitlements WHERE id='{f.Entitlement}'
                """);
            Assert.True((await loser.StartAsync(loserCaller, loserPrep, "retry-start", default)).IsSuccess);
            Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM billing_learner_seats WHERE entitlement_id='{f.Entitlement}'"));
            // Renewal opens a new period with its own seats.
            await ExecuteAsync($"ALTER TABLE service_entitlements DISABLE TRIGGER USER; UPDATE service_entitlements SET starts_at=now()-interval '10 minutes',ends_at=now()-interval '2 minutes' WHERE id='{f.Entitlement}'; ALTER TABLE service_entitlements ENABLE TRIGGER USER");
            await SeedPublishPaidEntitlement(f.Owner, f.Building, order: Random.Shared.NextInt64(100000, 900000));
            var renewal = (Guid)(await ScalarAsync($"SELECT id FROM service_entitlements WHERE building_id='{f.Building}' AND id<>'{f.Entitlement}'"))!;
            var renewed = await storeA.PrepareAsync(callerA, new(f.Training, "Learn", "1.0.0"), "a-renewed", default);
            Assert.True((await storeA.StartAsync(callerA, renewed.Value!.Id, "a-renewed-start", default)).IsSuccess);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM billing_learner_seats WHERE entitlement_id='{renewal}'"));
            Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM billing_learner_seats WHERE entitlement_id='{f.Entitlement}'"));
        });
    }

    [PostgresFact]
    public async Task Learner_continuation_syncs_after_logout_and_loss_of_access_but_never_starts()
    {
        var f = await PublishedTraining();
        var trainee = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var id = await Running(store, caller, f.Training, "Learn", "cont");
            var continuationId = (Guid)(await ScalarAsync($"SELECT continuation_id FROM sessions WHERE id='{id}'"))!;
            // Logout, entitlement expiry and lost Building access do not block sync of the started session.
            await ExecuteAsync($"""
                UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{trainee.Family}';
                UPDATE buildings SET visibility='Private' WHERE id='{f.Building}';
                ALTER TABLE service_entitlements DISABLE TRIGGER USER; UPDATE service_entitlements SET ends_at=now()-interval '1 second' WHERE id='{f.Entitlement}'; ALTER TABLE service_entitlements ENABLE TRIGGER USER;
                """);
            Assert.Equal("UNAUTHORIZED", (await store.HeartbeatAsync(caller, id, default)).Error?.Code);
            var offline = new LearnerCaller(trainee.Id, null, continuationId);
            Assert.Single((await store.RecordEventsAsync(offline, id, new([LearnerEvent(1, "ExitReached", 1000)]), default)).Value!.Accepted);
            Assert.Equal("CONTINUATION_NOT_ALLOWED", (await store.PrepareAsync(offline, new(f.Training, "Learn", "1.0.0"), "offline-prepare", default)).Error?.Code);
            Assert.Equal("CONTINUATION_INVALID", (await store.HeartbeatAsync(new(trainee.Id, null, Guid.NewGuid()), id, default)).Error?.Code);
            var stranger = await SeedTrainee();
            Assert.Equal("NOT_FOUND", (await store.HeartbeatAsync(new(stranger.Id, null, continuationId), id, default)).Error?.Code);
            await ExecuteAsync($"UPDATE sessions SET continuation_expires_at=now()-interval '1 second' WHERE id='{id}'");
            Assert.Equal("CONTINUATION_INVALID", (await store.HeartbeatAsync(offline, id, default)).Error?.Code);
            // A live login reissues continuation for the same owner; the previous one stops working.
            var relogin = Guid.NewGuid();
            await ExecuteAsync($"INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{trainee.Id}','{relogin}','{Guid.NewGuid():N}{Guid.NewGuid():N}',now(),now()+interval '1 hour')");
            var reissued = await store.ContinuationAsync(new(trainee.Id, relogin, null), id, default);
            Assert.True(reissued.IsSuccess, reissued.Error?.Code);
            var fresh = (Guid)(await ScalarAsync($"SELECT continuation_id FROM sessions WHERE id='{id}'"))!;
            Assert.NotEqual(continuationId, fresh);
            Assert.Equal(200, (await store.CompleteAsync(new(trainee.Id, null, fresh), id, new(1, "Finished"), "cont-complete", default)).Value.Status);
            Assert.Equal("NotAssessed", (await store.ResultAsync(new(trainee.Id, null, fresh), id, default)).Value!.Outcome);
            Assert.Equal("Completed", (await store.GetAsync(new(trainee.Id, relogin, null), id, default)).Value!.Status);
        });
    }

    [PostgresFact]
    public async Task Learner_access_mode_rubric_qr_and_reconcile_are_scoped()
    {
        var f = await PublishedTraining(publicBuilding: false);
        var trainee = await SeedTrainee(); var other = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            Assert.Equal("BUILDING_ACCESS_REQUIRED", (await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "private", default)).Error?.Code);
            await ExecuteAsync($"INSERT INTO building_participation_grants(building_id,trainee_user_id,access_revision) SELECT id,'{trainee.Id}',access_revision FROM buildings WHERE id='{f.Building}'");
            var prepared = await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "granted", default);
            Assert.True(prepared.IsSuccess, prepared.Error?.Code);
            // Rotating the code moves access_revision: the old grant no longer authorizes start.
            await ExecuteAsync($"UPDATE buildings SET participation_code_hash=repeat('b',64) WHERE id='{f.Building}'");
            Assert.Equal("BUILDING_ACCESS_REQUIRED", (await store.StartAsync(caller, prepared.Value!.Id, "granted-start", default)).Error?.Code);
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM billing_learner_seats"));
            await ExecuteAsync($"UPDATE buildings SET visibility='Public' WHERE id='{f.Building}'");
            Assert.Equal("RUBRIC_NOT_SUPPORTED", (await store.PrepareAsync(caller, new(f.Training, "Assessment", "1.0.0"), "rubric", default)).Error?.Code);
            Assert.Equal("RUNTIME_INCOMPATIBLE", (await store.PrepareAsync(caller, new(f.Training, "Learn", "9.9.9"), "runtime", default)).Error?.Code);
            await ExecuteAsync($"UPDATE trainings SET allowed_modes=ARRAY['Assessment'] WHERE id='{f.Training}'");
            Assert.Equal("MODE_NOT_ALLOWED", (await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "mode", default)).Error?.Code);
            var reconciled = await store.ReconcileAsync(caller, new([prepared.Value.Id, Guid.NewGuid()], ["granted"]), default);
            Assert.Equal([prepared.Value.Id], reconciled.Value!.Select(x => x.Id).Distinct());
            Assert.Empty((await store.ReconcileAsync(new(other.Id, other.Family, null), new([prepared.Value.Id], ["granted"]), default)).Value!);
            Assert.Equal("NOT_FOUND", (await store.GetAsync(new(other.Id, other.Family, null), prepared.Value.Id, default)).Error?.Code);

            var qr = new BuildingQrCodes(db);
            var created = await qr.CreateAsync(f.Owner, f.OwnerFamily, f.Building, new("Lobby"), default);
            Assert.True(created.IsSuccess, created.Error?.Code);
            Assert.Equal(64, (await ScalarAsync($"SELECT length(token_hash) FROM building_qr_codes WHERE id='{created.Value!.Code.Id}'")));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM building_qr_codes WHERE token_hash='{created.Value.Token}'"));
            var resolved = await qr.ResolveAsync(trainee.Id, trainee.Family, created.Value.Token, default);
            Assert.Equal(f.Building, resolved.Value!.BuildingId); Assert.True(resolved.Value.HasAccess);
            Assert.Equal("FORBIDDEN", (await qr.CreateAsync(trainee.Id, trainee.Family, f.Building, new(null), default)).Error?.Code);
            var rotated = await qr.RotateAsync(f.Owner, f.OwnerFamily, f.Building, created.Value.Code.Id, new(null), default);
            Assert.Equal("Lobby", rotated.Value!.Code.Label);
            Assert.Equal("QR_NOT_FOUND", (await qr.ResolveAsync(trainee.Id, trainee.Family, created.Value.Token, default)).Error?.Code);
            Assert.True((await qr.ResolveAsync(trainee.Id, trainee.Family, rotated.Value.Token, default)).IsSuccess);
            Assert.True((await qr.RevokeAsync(f.Owner, f.OwnerFamily, f.Building, rotated.Value.Code.Id, default)).IsSuccess);
            Assert.Equal("QR_NOT_FOUND", (await qr.ResolveAsync(trainee.Id, trainee.Family, rotated.Value.Token, default)).Error?.Code);
            Assert.Equal(2, (await qr.ListAsync(f.Owner, f.OwnerFamily, f.Building, default)).Value!.Count);
        });
    }

    [PostgresFact]
    public async Task Learner_HTTP_continuation_token_is_bound_to_one_session()
    {
        var f = await PublishedTraining();
        var trainee = await SeedTrainee();
        var email = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{trainee.Id}'"))!;
        var tokens = await LoginAsync(email);
        using var enabled = factory!.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LearnerSessions:Enabled"] = "true", ["LearnerSessions:SigningKey"] = LearnerOptions.SigningKey, ["LearnerSessions:ContinuationSigningKey"] = LearnerOptions.ContinuationSigningKey
        })));
        using var http = enabled.CreateClient(); http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        HttpRequestMessage Keyed(HttpMethod method, string url, object? body, string key)
        {
            var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key); return request;
        }
        var prepared = await http.SendAsync(Keyed(HttpMethod.Post, "/api/training/sessions", new { trainingId = f.Training, mode = "Learn", runtimeVersion = "1.0.0" }, "http-prepare"));
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var id = (await prepared.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(prepared.Headers.Location)).StatusCode);
        var started = await http.SendAsync(Keyed(HttpMethod.Post, $"/api/training/sessions/{id}/start", null, "http-start"));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var start = (await started.Content.ReadFromJsonAsync<StartTrainingSessionResponse>(Json))!;
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"/api/training/sessions/{id}/launched", new { generation = start.Launch.Generation })).StatusCode);
        using var offline = enabled.CreateClient(); offline.DefaultRequestHeaders.Authorization = new("Bearer", start.Continuation.Token);
        var events = await offline.PostAsJsonAsync($"/api/training/sessions/{id}/events:batch", new { events = new[] { new { eventId = Guid.NewGuid(), sequence = 1, schemaVersion = "1", type = "ExitReached", occurredAt = DateTimeOffset.UtcNow, elapsedMs = 100, payload = new { } } } });
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await offline.GetAsync($"/api/training/sessions/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await offline.PostAsync("/api/training/reconcile", JsonContent.Create(new { sessionIds = new[] { id } }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await offline.GetAsync("/api/auth/me")).StatusCode);
        var completed = await offline.SendAsync(Keyed(HttpMethod.Post, $"/api/training/sessions/{id}/complete", new { lastEventSequence = 1, endReason = "Finished" }, "http-complete"));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("NotAssessed", (await offline.GetFromJsonAsync<JsonObject>($"/api/training/sessions/{id}/result"))!["outcome"]!.GetValue<string>());
    }
}
