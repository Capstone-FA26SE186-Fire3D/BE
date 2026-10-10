using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static readonly PlaytestOptions HandoffOptions = new()
    {
        Enabled = true, SigningKey = "fixture-only-playtest-key-32-bytes-never-production",
        HandoffKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
        HandoffDeepLinkBaseUrl = "fet3d://playtest/handoff"
    };

    private sealed record PlaytestFixture(Guid Owner, Guid Building, Guid Scenario, Guid Version, Guid Revision, Guid Web, Guid Mobile, Guid Trial);

    private async Task<PlaytestFixture> SeedPlaytestRuntime(int units = 1)
    {
        var f = await SeedReadyPackage("PlaytestPackage");
        var web = await SeedPlaytestFamily(f.Owner); var mobile = await SeedPlaytestFamily(f.Owner);
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        var scenario = (Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{f.Version}'"))!;
        return new(f.Owner, building, scenario, f.Version, f.Revision, web, mobile, await SeedTrial(f.Owner, building, units));
    }

    private async Task WithPlaytestRuntimeGates(Func<string, Task> action)
    {
        var login = "playtest_runtime_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION playtest_lifecycle_gate(text,uuid,uuid,uuid,uuid,jsonb,text),playtest_runtime_gate(text,uuid,uuid,uuid,jsonb,text) TO {login}");
        try { await action(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString); }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }

    private static PlaytestEvent Event(long sequence, Guid? id = null, string payload = "{\"step\":1}") =>
        new(id ?? Guid.NewGuid(), sequence, "1", "move", DateTimeOffset.UtcNow, JsonDocument.Parse(payload).RootElement.Clone());

    [PostgresFact]
    public async Task Playtest_handoff_binds_mobile_session_then_launch_sync_and_complete_once()
    {
        var f = await SeedPlaytestRuntime();
        var stranger = await SeedReadyPackage("PlaytestPackage"); var strangerFamily = await SeedPlaytestFamily(stranger.Owner);
        await WithPlaytestRuntimeGates(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new PlaytestLifecycle(db, Options.Create(HandoffOptions));
            var prepared = await store.PrepareAsync(f.Owner, f.Web, f.Building, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "rt-prepare", default);
            Assert.True(prepared.IsSuccess, prepared.Error?.Code);
            var id = prepared.Value!.Id;

            var handoff = await store.CreateHandoffAsync(f.Owner, f.Web, id, "rt-handoff", default);
            Assert.True(handoff.IsSuccess, handoff.Error?.Code);
            Assert.Equal(43, handoff.Value!.Code.Length);
            Assert.Equal($"fet3d://playtest/handoff?code={handoff.Value.Code}", handoff.Value.DeepLink);
            Assert.Equal(TimeSpan.FromMinutes(5), handoff.Value.ExpiresAt - (DateTime)(await ScalarAsync($"SELECT created_at FROM playtest_handoffs WHERE id='{handoff.Value.HandoffId}'"))!);
            // Lost response: same key returns the same code; only the hash and ciphertext are stored, never the code.
            Assert.Equal(handoff.Value.Code, (await store.CreateHandoffAsync(f.Owner, f.Web, id, "rt-handoff", default)).Value!.Code);
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM playtest_handoffs WHERE position(convert_to('{handoff.Value.Code}','UTF8') in COALESCE(code_ciphertext,''::bytea))>0"));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM playtest_command_receipts WHERE result::text LIKE '%{handoff.Value.Code}%' OR result ? 'codeCiphertext'"));

            Assert.Equal("PLAYTEST_HANDOFF_INVALID", (await store.RedeemHandoffAsync(stranger.Owner, strangerFamily, new(handoff.Value.Code), default)).Error?.Code);
            Assert.Equal("UNAUTHORIZED", (await store.RedeemHandoffAsync(adminId, f.Web, new(handoff.Value.Code), default)).Error?.Code);
            Assert.Equal("FORBIDDEN", (await store.RedeemHandoffAsync(adminId, await SeedPlaytestFamily(adminId), new(handoff.Value.Code), default)).Error?.Code);
            var redeemed = await store.RedeemHandoffAsync(f.Owner, f.Mobile, new(handoff.Value.Code), default);
            Assert.True(redeemed.IsSuccess, redeemed.Error?.Code);
            Assert.True(redeemed.Value!.BoundToMobile); Assert.True(redeemed.Value.LaunchSession); Assert.True(redeemed.Value.Recovery.CanStart);
            Assert.True((await store.RedeemHandoffAsync(f.Owner, f.Mobile, new(handoff.Value.Code), default)).IsSuccess);
            Assert.Equal("PLAYTEST_HANDOFF_USED", (await store.RedeemHandoffAsync(f.Owner, f.Web, new(handoff.Value.Code), default)).Error?.Code);
            Assert.False((await store.GetAsync(f.Owner, f.Web, id, default)).Value!.LaunchSession);
            Assert.Equal(0, await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{f.Trial}'"));

            Assert.Equal("PLAYTEST_SESSION_MISMATCH", (await store.StartAsync(f.Owner, f.Web, id, new("1.0.0"), "rt-start-web", default)).Error?.Code);
            var started = await store.StartAsync(f.Owner, f.Mobile, id, new("1.0.0"), "rt-start", default);
            Assert.True(started.IsSuccess, started.Error?.Code);
            Assert.Equal(1, started.Value!.Generation);
            Assert.Equal("Launching", await ScalarAsync($"SELECT status FROM playtest_sessions WHERE id='{id}'"));
            Assert.Equal(1, await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{f.Trial}'"));
            Assert.Equal("PLAYTEST_ALREADY_STARTED", (await store.CreateHandoffAsync(f.Owner, f.Web, id, "rt-handoff-late", default)).Error?.Code);

            Assert.Equal("PLAYTEST_SESSION_MISMATCH", (await store.ReissueGrantAsync(f.Owner, f.Web, id, "rt-reissue-web", default)).Error?.Code);
            var reissued = await store.ReissueGrantAsync(f.Owner, f.Mobile, id, "rt-reissue", default);
            Assert.True(reissued.IsSuccess, reissued.Error?.Code);
            Assert.Equal(2, reissued.Value!.Generation);
            Assert.Equal(2, (await store.ReissueGrantAsync(f.Owner, f.Mobile, id, "rt-reissue", default)).Value!.Generation);
            var principal = new JwtSecurityTokenHandler().ValidateToken(reissued.Value.LaunchGrant, new TokenValidationParameters { ValidIssuer = HandoffOptions.Issuer, ValidAudience = HandoffOptions.Audience, ClockSkew = TimeSpan.Zero, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(HandoffOptions.SigningKey)) }, out _);
            Assert.Equal("2", principal.FindFirstValue("grant_generation")); Assert.Equal(f.Mobile.ToString(), principal.FindFirstValue("sid"));
            Assert.Equal(1, await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{f.Trial}'"));

            Assert.Equal("PLAYTEST_GRANT_STALE", (await store.LaunchedAsync(f.Owner, f.Mobile, id, new(1), default)).Error?.Code);
            Assert.Equal("Running", (await store.LaunchedAsync(f.Owner, f.Mobile, id, new(2), default)).Value!.Status);
            Assert.True((await store.LaunchedAsync(f.Owner, f.Mobile, id, new(2), default)).IsSuccess);
            Assert.Equal("PLAYTEST_ALREADY_LAUNCHED", (await store.ReissueGrantAsync(f.Owner, f.Mobile, id, "rt-reissue-late", default)).Error?.Code);

            Assert.Equal("PLAYTEST_SESSION_MISMATCH", (await store.HeartbeatAsync(f.Owner, f.Web, id, default)).Error?.Code);
            Assert.Equal("Running", (await store.HeartbeatAsync(f.Owner, f.Mobile, id, default)).Value!.Status);

            var first = Event(1);
            var batch = (await store.RecordEventsAsync(f.Owner, f.Mobile, id, new([first, Event(3)]), default)).Value!;
            Assert.Equal(2, batch.Accepted.Count); Assert.Equal(1, batch.AcknowledgedSequence); Assert.Equal([new SequenceRange(2, 2)], batch.MissingRanges);
            var retry = (await store.RecordEventsAsync(f.Owner, f.Mobile, id, new([first, first with { EventId = Guid.NewGuid() }, Event(1, first.EventId, "{\"step\":2}")]), default));
            Assert.Equal("VALIDATION_ERROR", retry.Error?.Code);
            // Lost ACK: resending the same event is a duplicate; reusing a sequence or changing content is a conflict.
            var mixed = (await store.RecordEventsAsync(f.Owner, f.Mobile, id, new([first, Event(3)]), default)).Value!;
            Assert.Equal([first.EventId], mixed.Duplicates);
            Assert.Equal("EVENT_SEQUENCE_CONFLICT", Assert.Single(mixed.Conflicts).Code);
            Assert.Equal("EVENT_HASH_CONFLICT", Assert.Single((await store.RecordEventsAsync(f.Owner, f.Mobile, id, new([Event(9, first.EventId, "{\"step\":2}")]), default)).Value!.Conflicts).Code);
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM playtest_events WHERE playtest_id='{id}' AND sequence=9"));
            Assert.Equal("PLAYTEST_EVENTS_INCOMPLETE", (await store.CompleteAsync(f.Owner, f.Mobile, id, new(3), "rt-complete", default)).Error?.Code);
            Assert.Equal(3, (await store.RecordEventsAsync(f.Owner, f.Mobile, id, new([Event(2)]), default)).Value!.AcknowledgedSequence);

            var summary = JsonDocument.Parse("{\"reachedExit\":true}").RootElement;
            var completed = await store.CompleteAsync(f.Owner, f.Mobile, id, new(3, summary), "rt-complete", default);
            Assert.True(completed.IsSuccess, completed.Error?.Code);
            Assert.Equal("Completed", completed.Value!.Status);
            Assert.Equal("Completed", (await store.CompleteAsync(f.Owner, f.Mobile, id, new(3, summary), "rt-complete", default)).Value!.Status);
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", (await store.CompleteAsync(f.Owner, f.Mobile, id, new(2), "rt-complete", default)).Error?.Code);
            Assert.Equal("PLAYTEST_TERMINAL", (await store.CancelAsync(f.Owner, f.Web, id, "rt-cancel", default)).Error?.Code);
            Assert.Equal("PLAYTEST_TERMINAL", (await store.HeartbeatAsync(f.Owner, f.Mobile, id, default)).Error?.Code);
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE playtest_events SET payload='{{}}' WHERE playtest_id='{id}'"));
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE playtest_runtime_state SET terminal_status='Cancelled' WHERE playtest_id='{id}'"));
            // Playtests stay out of learner seats/analytics and never refund or re-consume Trial.
            Assert.Equal(1, await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{f.Trial}'"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM sessions"));
        });
    }

    [PostgresFact]
    public async Task Playtest_handoff_rotation_expiry_issuer_logout_and_terminal_race()
    {
        var f = await SeedPlaytestRuntime(units: 2);
        await WithPlaytestRuntimeGates(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new PlaytestLifecycle(db, Options.Create(HandoffOptions));
            var id = (await store.PrepareAsync(f.Owner, f.Web, null, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "rot-prepare", default)).Value!.Id;
            var old = (await store.CreateHandoffAsync(f.Owner, f.Web, id, "rot-1", default)).Value!;
            var current = (await store.CreateHandoffAsync(f.Owner, f.Web, id, "rot-2", default)).Value!;
            Assert.Equal("PLAYTEST_HANDOFF_REVOKED", (await store.RedeemHandoffAsync(f.Owner, f.Mobile, new(old.Code), default)).Error?.Code);
            Assert.Equal("PLAYTEST_HANDOFF_EXPIRED", (await store.CreateHandoffAsync(f.Owner, f.Web, id, "rot-1", default)).Error?.Code);
            await ExecuteAsync($"UPDATE playtest_handoffs SET created_at=now()-interval '6 minutes',expires_at=now()-interval '1 minute' WHERE id='{current.HandoffId}'");
            Assert.Equal("PLAYTEST_HANDOFF_EXPIRED", (await store.RedeemHandoffAsync(f.Owner, f.Mobile, new(current.Code), default)).Error?.Code);
            Assert.Equal("PLAYTEST_HANDOFF_INVALID", (await store.RedeemHandoffAsync(f.Owner, f.Mobile, new(new string('A', 43)), default)).Error?.Code);

            var fresh = (await store.CreateHandoffAsync(f.Owner, f.Web, id, "rot-3", default)).Value!;
            await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{f.Web}'");
            Assert.Equal("PLAYTEST_HANDOFF_ISSUER_INVALID", (await store.RedeemHandoffAsync(f.Owner, f.Mobile, new(fresh.Code), default)).Error?.Code);
            Assert.Equal("UNAUTHORIZED", (await store.GetAsync(f.Owner, f.Web, id, default)).Error?.Code);

            // Without handoff the preparing session launches; the revoked Web session cannot, so prepare again from Mobile.
            var second = (await store.PrepareAsync(f.Owner, f.Mobile, null, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "rot-prepare-2", default)).Value!.Id;
            var launch = (await store.StartAsync(f.Owner, f.Mobile, second, new("1.0.0"), "rot-start", default)).Value!;
            Assert.Equal("Running", (await store.LaunchedAsync(f.Owner, f.Mobile, second, new(launch.Generation), default)).Value!.Status);
            await using var otherDb = BuildingContext(runtime);
            var other = new PlaytestLifecycle(otherDb, Options.Create(HandoffOptions));
            var race = await Task.WhenAll(store.CompleteAsync(f.Owner, f.Mobile, second, new(0), "rot-complete", default), other.CancelAsync(f.Owner, f.Mobile, second, "rot-cancel", default));
            Assert.Single(race, x => x.IsSuccess);
            Assert.Equal("PLAYTEST_TERMINAL", Assert.Single(race, x => !x.IsSuccess).Error!.Code);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM playtest_runtime_state WHERE playtest_id='{second}' AND terminal_status IS NOT NULL"));
            Assert.Equal(1, await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{f.Trial}'"));

            // A cancelled playtest before launch keeps its consumed unit and its grant can no longer confirm launch.
            var third = (await store.PrepareAsync(f.Owner, f.Mobile, null, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "rot-prepare-3", default)).Value!.Id;
            var thirdLaunch = (await store.StartAsync(f.Owner, f.Mobile, third, new("1.0.0"), "rot-start-3", default)).Value!;
            Assert.Equal("Cancelled", (await store.CancelAsync(f.Owner, f.Mobile, third, "rot-cancel-3", default)).Value!.Status);
            Assert.Equal("PLAYTEST_NOT_LAUNCHING", (await store.LaunchedAsync(f.Owner, f.Mobile, third, new(thirdLaunch.Generation), default)).Error?.Code);
            Assert.Equal(2, await ScalarAsync($"SELECT playtest_units_used FROM service_entitlements WHERE id='{f.Trial}'"));
        });
    }

    [PostgresFact]
    public async Task Playtest_HTTP_status_location_handoff_configuration_and_openapi()
    {
        var f = await SeedPlaytestRuntime();
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{f.Owner}'");
        var email = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{f.Owner}'"))!;
        var tokens = await LoginAsync(email);
        using var plain = factory!.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Playtest:Enabled"] = "true", ["Playtest:SigningKey"] = HandoffOptions.SigningKey })));
        using var browser = plain.CreateClient(); browser.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        var prepare = new HttpRequestMessage(HttpMethod.Post, $"/api/scenarios/{f.Scenario}/playtests") { Content = JsonContent.Create(new { revisionId = f.Revision, scenarioVersionId = f.Version }) };
        prepare.Headers.Add("Idempotency-Key", "http-rt-prepare");
        var prepared = await browser.SendAsync(prepare);
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var status = await browser.GetAsync(prepared.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = (await status.Content.ReadFromJsonAsync<PlaytestStatusResponse>(Json))!;
        Assert.Equal("Created", body.Status); Assert.True(body.LaunchSession); Assert.Null(body.Grant);
        Assert.DoesNotContain("family", await status.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var handoff = new HttpRequestMessage(HttpMethod.Post, $"/api/playtests/{body.Id}/handoffs"); handoff.Headers.Add("Idempotency-Key", "http-rt-handoff");
        var unavailable = await browser.SendAsync(handoff);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("PLAYTEST_HANDOFF_UNAVAILABLE", await unavailable.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/playtests/{Guid.NewGuid()}")).StatusCode);

        using var configured = plain.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Playtest:HandoffKey"] = HandoffOptions.HandoffKey, ["Playtest:HandoffDeepLinkBaseUrl"] = HandoffOptions.HandoffDeepLinkBaseUrl })));
        using var web = configured.CreateClient(); web.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        handoff = new HttpRequestMessage(HttpMethod.Post, $"/api/playtests/{body.Id}/handoffs"); handoff.Headers.Add("Idempotency-Key", "http-rt-handoff");
        var created = await web.SendAsync(handoff);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var code = (await created.Content.ReadFromJsonAsync<PlaytestHandoffResponse>(Json))!.Code;
        var mobileTokens = await LoginAsync(email);
        web.DefaultRequestHeaders.Authorization = new("Bearer", mobileTokens.AccessToken);
        var redeemed = await web.PostAsJsonAsync("/api/playtests/handoffs/redeem", new { code });
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.True((await redeemed.Content.ReadFromJsonAsync<PlaytestStatusResponse>(Json))!.LaunchSession);
        web.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        Assert.False((await web.GetFromJsonAsync<PlaytestStatusResponse>($"/api/playtests/{body.Id}", Json))!.LaunchSession);
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsJsonAsync("/api/playtests/handoffs/redeem", new { code })).StatusCode);

        using var doc = JsonDocument.Parse(await web.GetStringAsync("/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/playtests/{playtestId}", "/api/playtests/{playtestId}/handoffs", "/api/playtests/handoffs/redeem", "/api/playtests/{playtestId}/launch-grants", "/api/playtests/{playtestId}/launched", "/api/playtests/{playtestId}/heartbeat", "/api/playtests/{playtestId}/events:batch", "/api/playtests/{playtestId}/complete", "/api/playtests/{playtestId}/cancel" })
            Assert.True(paths.TryGetProperty(path, out _), path);
        Assert.Contains(paths.GetProperty("/api/playtests/{playtestId}/complete").GetProperty("post").GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "Idempotency-Key" && p.GetProperty("required").GetBoolean());
    }
}
