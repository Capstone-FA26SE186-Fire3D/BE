using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Learning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Learner_HTTP_history_heartbeat_package_continuation_reconcile_and_building_qr()
    {
        var f = await PublishedTraining();
        var trainee = await SeedTrainee();
        var email = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{trainee.Id}'"))!;
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{f.Owner}'");
        var ownerEmail = (string)(await ScalarAsync($"SELECT email FROM users WHERE id='{f.Owner}'"))!;
        using var enabled = factory!.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LearnerSessions:Enabled"] = "true", ["LearnerSessions:SigningKey"] = LearnerOptions.SigningKey, ["LearnerSessions:ContinuationSigningKey"] = LearnerOptions.ContinuationSigningKey
        })));
        using var http = enabled.CreateClient(); http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync(email)).AccessToken);
        using var owner = enabled.CreateClient(); owner.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync(ownerEmail)).AccessToken);
        HttpRequestMessage Keyed(string url, object? body, string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key); return request;
        }
        var id = (await (await http.SendAsync(Keyed("/api/training/sessions", new { trainingId = f.Training, mode = "Learn", runtimeVersion = "1.0.0" }, "routes-prepare"))).Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var start = (await (await http.SendAsync(Keyed($"/api/training/sessions/{id}/start", null, "routes-start"))).Content.ReadFromJsonAsync<StartTrainingSessionResponse>(Json))!;
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"/api/training/sessions/{id}/launched", new { generation = start.Launch.Generation })).StatusCode);

        var history = (await http.GetFromJsonAsync<JsonObject>($"/api/training/sessions?status=Running&buildingId={f.Building}&pageSize=5"))!;
        Assert.Equal(1, history["totalCount"]!.GetValue<int>()); Assert.Equal(id, history["items"]![0]!["sessionId"]!.GetValue<Guid>());
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonObject>("/api/training/sessions?status=Completed"))!["totalCount"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/training/sessions?status=Done")).StatusCode);

        using var offline = enabled.CreateClient(); offline.DefaultRequestHeaders.Authorization = new("Bearer", start.Continuation.Token);
        Assert.Equal(HttpStatusCode.OK, (await offline.PostAsync($"/api/training/sessions/{id}/heartbeat", null)).StatusCode);
        var mismatch = await offline.PostAsync($"/api/training/sessions/{Guid.NewGuid()}/heartbeat", null);
        Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        Assert.Equal("CONTINUATION_SESSION_MISMATCH", (await mismatch.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());

        var package = await http.GetAsync($"/api/training/sessions/{id}/package");
        // The fixture manifest predates fet3d.package/1, so the started session is served the explicit format code.
        Assert.Equal(HttpStatusCode.Conflict, package.StatusCode);
        Assert.Equal("PACKAGE_FORMAT_UNSUPPORTED", (await package.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/training/sessions/{Guid.NewGuid()}/package")).StatusCode);

        // A reissued continuation replaces the previous token for the same session.
        var reissued = await http.PostAsync($"/api/training/sessions/{id}/continuation", null);
        Assert.Equal(HttpStatusCode.OK, reissued.StatusCode);
        var continuation = (await reissued.Content.ReadFromJsonAsync<TrainingContinuation>(Json))!;
        Assert.NotEqual(start.Continuation.Token, continuation.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await offline.PostAsync($"/api/training/sessions/{id}/heartbeat", null)).StatusCode);
        using var renewed = enabled.CreateClient(); renewed.DefaultRequestHeaders.Authorization = new("Bearer", continuation.Token);
        Assert.Equal(HttpStatusCode.OK, (await renewed.PostAsync($"/api/training/sessions/{id}/heartbeat", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/training/sessions/{Guid.NewGuid()}/continuation", null)).StatusCode);

        var reconciled = (await (await http.PostAsJsonAsync("/api/training/reconcile", new { sessionIds = new[] { id, Guid.NewGuid() }, idempotencyKeys = new[] { "routes-prepare" } })).Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Equal([id], reconciled.Select(x => x!["id"]!.GetValue<Guid>()).Distinct());

        // Building QR: the owner manages codes, a Trainee resolves them and lists the published Trainings.
        var created = await owner.PostAsJsonAsync($"/api/buildings/{f.Building}/qr-codes", new { label = "Lobby" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/buildings/{f.Building}/qr-codes", created.Headers.Location!.OriginalString);
        var code = (await created.Content.ReadFromJsonAsync<BuildingQrCreated>(Json))!;
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/buildings/{f.Building}/qr-codes", new { label = "Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync($"/api/buildings/{f.Building}/qr-codes", new { label = "Mine" })).StatusCode);
        var rotated = await owner.PostAsJsonAsync($"/api/buildings/{f.Building}/qr-codes/{code.Code.Id}/rotate", new { label = (string?)null });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var current = (await rotated.Content.ReadFromJsonAsync<BuildingQrCreated>(Json))!;
        Assert.Equal(("Lobby", code.Code.Id), (current.Code.Label, current.Code.ReplacedId));
        var listed = (await owner.GetFromJsonAsync<JsonObject>($"/api/buildings/{f.Building}/qr-codes?page=1&pageSize=10"))!;
        Assert.Equal(2, listed["total"]!.GetValue<int>());
        var resolved = (await http.GetFromJsonAsync<BuildingQrResolution>($"/api/qr/{current.Token}", Json))!;
        Assert.Equal((f.Building, true), (resolved.BuildingId, resolved.HasAccess));
        var trainings = (await http.GetFromJsonAsync<JsonArray>($"/api/qr/{current.Token}/trainings"))!;
        Assert.Contains(trainings, x => x!["id"]!.GetValue<Guid>() == f.Training);
        var replaced = await http.GetAsync($"/api/qr/{code.Token}/trainings");
        Assert.Equal(HttpStatusCode.Gone, replaced.StatusCode);
        Assert.Equal("QR_REVOKED", (await replaced.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/buildings/{f.Building}/qr-codes/{current.Code.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await http.GetAsync($"/api/qr/{current.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/qr/{new string('A', 43)}/trainings")).StatusCode);
    }

    [PostgresFact]
    public async Task Training_analytics_CSV_export_routes_are_role_scoped_audited_and_daily_limited()
    {
        var tenant = await SeedAiTenant(); var other = await SeedAiTenant();
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{tenant.User}'");
        using var org = factory!.CreateClient(); org.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync($"ai-{tenant.User:N}@example.test")).AccessToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        async Task<string> Csv(HttpClient http, string url)
        {
            using var response = await http.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
            Assert.EndsWith(".csv", response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
            return Encoding.UTF8.GetString(bytes[3..]);
        }
        var own = await Csv(org, "/api/organizations/me/analytics/training/export?groupBy=week");
        var header = own.Split("\r\n")[0];
        Assert.False(string.IsNullOrWhiteSpace(header));
        Assert.DoesNotContain("email", own, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(header, (await Csv(client, "/api/admin/analytics/training/export")).Split("\r\n")[0]);
        Assert.Equal(header, (await Csv(client, $"/api/admin/organizations/{tenant.Org}/analytics/training/export?groupBy=building")).Split("\r\n")[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/organizations/{Guid.NewGuid()}/analytics/training/export")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.GetAsync("/api/organizations/me/analytics/training/export?groupBy=hour")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await org.GetAsync("/api/admin/analytics/training/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await org.GetAsync($"/api/admin/organizations/{other.Org}/analytics/training/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/organizations/me/analytics/training/export")).StatusCode);
        Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM audit_logs WHERE target_entity='AnalyticsExport'"));

        // Ten exports per account per UTC day; the eleventh is refused with Retry-After.
        for (var i = 0; i < 8; i++) await Csv(client, "/api/admin/analytics/training/export");
        using var limited = await client.GetAsync("/api/admin/analytics/training/export");
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("EXPORT_DAILY_LIMIT_EXCEEDED", (await limited.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(10L, await ScalarAsync($"SELECT count(*) FROM analytics_exports WHERE actor_id='{adminId}'"));
    }
}
