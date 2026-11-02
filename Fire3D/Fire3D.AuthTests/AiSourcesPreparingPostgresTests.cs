using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private sealed class RecordingIndex : IAiSourceIndex
    {
        public readonly List<AiSourcePin> Enqueued = [];
        public readonly Dictionary<Guid, AiIndexInfo> Known = [];
        public void Record(IEnumerable<AiSourceIndexState> states) { foreach (var s in states) Known[s.SourceId] = AiIndexStatus.From(s); }
        public AiIndexInfo Of(Guid sourceId) => Known.TryGetValue(sourceId, out var info) ? info : new(AiIndexStatus.Unknown, null);
        public void Enqueue(AiSourcePin pin) => Enqueued.Add(pin);
    }

    private sealed class FakeWarmer : IAiSourceWarmer
    {
        public Func<IReadOnlyList<AiSourcePin>, IReadOnlyList<AiSourceIndexState>?> Answer = _ => [];
        public readonly List<IReadOnlyList<AiSourcePin>> Calls = [];
        public Task<IReadOnlyList<AiSourceIndexState>?> WarmAsync(IReadOnlyList<AiSourcePin> pins, CancellationToken ct) { Calls.Add(pins); return Task.FromResult(Answer(pins)); }
    }

    private static HttpResponseMessage AiBusy(int status, string? code, int? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = JsonContent.Create(code is null ? new { } : (object)new { code, message = "retry later" }) };
        if (retryAfter is { } seconds) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
        return response;
    }

    [PostgresFact]
    public async Task Organization_ai_fails_a_question_asked_while_sources_are_being_prepared_once_and_says_how_long_to_wait()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant();
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            var source = await ApprovedSource(gates, admin);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("Answer", "tokens", 100, 1000, 5), "preparing-policy", null, default)).IsSuccess);
            await SeedAiGrant(tenant.Org, 500);

            // 503 AI_SOURCES_PREPARING: one dispatch, no retries, the reservation is returned, the client learns the wait.
            var preparing = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Ready yet?"), "preparing", default)).Value.GetProperty("requestId").GetGuid();
            var fake = new FakeFastApi { Respond = (_, _) => AiBusy(503, "AI_SOURCES_PREPARING") };
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Single(fake.Calls);
            Assert.False(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Single(fake.Calls);
            var org = (await gates.OrganizationAsync("Get", tenant.User, tenant.Family, preparing, new { }, null, default)).Value;
            Assert.Equal("Failed", org.GetProperty("status").GetString());
            Assert.Equal("AI_SOURCES_PREPARING", org.GetProperty("failureCode").GetString());
            Assert.Equal(AiRefusal.DefaultPreparingRetrySeconds, org.GetProperty("retryAfterSeconds").GetInt32());
            Assert.False(org.TryGetProperty("providerResult", out _));
            Assert.Equal(100L, await AllocationUnits(preparing, "Released"));
            var refusal = (await gates.AdminAsync("GetRequest", adminId, admin, preparing, new { }, null, null, default)).Value.GetProperty("providerResult").GetProperty("refusal");
            Assert.Equal("503", refusal.GetProperty("httpStatus").GetString());
            Assert.Equal(AiRefusal.DefaultPreparingRetrySeconds, refusal.GetProperty("retryAfterSeconds").GetInt32());

            // Every other failure keeps retryAfterSeconds null.
            var hash = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Hash?"), "preparing-hash", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, _) => new((HttpStatusCode)422) { Content = JsonContent.Create(new { code = "AI_SOURCE_HASH_MISMATCH", message = "no match" }) };
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            var other = (await gates.OrganizationAsync("Get", tenant.User, tenant.Family, hash, new { }, null, default)).Value;
            Assert.Equal("AI_SOURCE_HASH_MISMATCH", other.GetProperty("failureCode").GetString());
            Assert.Equal(JsonValueKind.Null, other.GetProperty("retryAfterSeconds").ValueKind);

            // A 429 stays undelivered and waits at least what Retry-After asks (the worker backoff here is 1 s).
            var slow = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Slow?"), "preparing-429", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, _) => AiBusy(429, null, retryAfter: 90);
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal("Accepted", (string?)await ScalarAsync($"SELECT status FROM ai_requests WHERE id='{slow}'"));
            var wait = Convert.ToInt32(await ScalarAsync($"SELECT extract(epoch FROM (next_attempt_at-now()))::int FROM ai_requests WHERE id='{slow}'"));
            Assert.InRange(wait, 80, 91);

            var quick = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Quick?"), "preparing-429b", default)).Value.GetProperty("requestId").GetGuid();
            await ExecuteAsync($"UPDATE ai_requests SET next_attempt_at=now()+interval '1 hour' WHERE id='{slow}'");
            fake.Respond = (_, _) => AiBusy(429, null);
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.InRange(Convert.ToInt32(await ScalarAsync($"SELECT extract(epoch FROM (next_attempt_at-now()))::int FROM ai_requests WHERE id='{quick}'")), 0, 5);
        });
    }

    [PostgresFact]
    public async Task Admin_approval_warms_the_source_and_the_list_shows_what_the_ai_service_reported()
    {
        var index = new RecordingIndex();
        var warmer = new FakeWarmer { Answer = pins => pins.Select(p => new AiSourceIndexState(p.SourceId, p.Title.Contains("broken") ? "failed" : "ready", p.Title.Contains("broken") ? "AI_SOURCE_UNAVAILABLE" : null)).ToList() };
        using var ai = WithAiAdapter().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IAiSourceIndex>(); s.AddSingleton<IAiSourceIndex>(index);
            s.RemoveAll<IAiSourceWarmer>(); s.AddSingleton<IAiSourceWarmer>(warmer);
        }));
        using var admin = ai.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);

        async Task<Guid> Register(string title, string uri)
        {
            var created = await AiSend(admin, AiRequest(HttpMethod.Post, "/api/admin/knowledge-sources", new { visibility = "Common", title, versionLabel = "2022", sourceHash = Sha(title), sourceUri = uri }, "warm-" + Sha(title)[..8]));
            Assert.Equal(HttpStatusCode.Created, created.Status);
            return created.Body.GetProperty("id").GetGuid();
        }

        var good = await Register("Fire code good", "https://docs.example.test/good.pdf");
        var broken = await Register("Fire code broken", "https://docs.example.test/broken.pdf");
        var draft = await Register("Fire code draft", "https://docs.example.test/draft.pdf");
        Assert.Empty(index.Enqueued);

        // Approving asks the AI service to prepare the source, with the fields of a dispatch pin; a Draft is never warmed.
        foreach (var id in new[] { good, broken })
            Assert.Equal(HttpStatusCode.OK, (await AiSend(admin, AiRequest(HttpMethod.Post, $"/api/admin/knowledge-sources/{id}/approve", null, ifMatch: "\"1\""))).Status);
        Assert.Equal([good, broken], index.Enqueued.Select(p => p.SourceId));
        var pin = index.Enqueued[0];
        Assert.Equal(("Fire code good", "2022", Sha("Fire code good"), "https://docs.example.test/good.pdf"), (pin.Title, pin.VersionLabel, pin.ContentHash, pin.SourceUri));

        // Nothing reported yet: Unknown for approved sources; a Draft carries no index fields at all.
        var before = (await AiSend(admin, AiRequest(HttpMethod.Get, "/api/admin/knowledge-sources", null))).Body.GetProperty("items");
        Assert.Equal("Unknown", Item(before, good).GetProperty("aiIndexStatus").GetString());
        Assert.False(Item(before, draft).TryGetProperty("aiIndexStatus", out _));

        // Warm-all asks for every Approved source and records what comes back.
        var warm = await AiSend(admin, AiRequest(HttpMethod.Post, "/api/admin/knowledge-sources/warm", null));
        Assert.Equal(HttpStatusCode.OK, warm.Status);
        Assert.Equal((2, 1, 0, 1), (warm.Body.GetProperty("requested").GetInt32(), warm.Body.GetProperty("ready").GetInt32(), warm.Body.GetProperty("preparing").GetInt32(), warm.Body.GetProperty("failed").GetInt32()));
        Assert.Equal([good, broken], warmer.Calls.Single().Select(p => p.SourceId).OrderBy(x => x == good ? 0 : 1));
        Assert.DoesNotContain(warmer.Calls.Single(), p => p.SourceId == draft);

        var after = (await AiSend(admin, AiRequest(HttpMethod.Get, "/api/admin/knowledge-sources?status=Approved", null))).Body.GetProperty("items");
        Assert.Equal("Ready", Item(after, good).GetProperty("aiIndexStatus").GetString());
        Assert.False(Item(after, good).TryGetProperty("aiIndexError", out _));
        Assert.Equal("Failed", Item(after, broken).GetProperty("aiIndexStatus").GetString());
        Assert.Equal("AI_SOURCE_UNAVAILABLE", Item(after, broken).GetProperty("aiIndexError").GetString());
        var detail = await AiSend(admin, AiRequest(HttpMethod.Get, $"/api/admin/knowledge-sources/{broken}", null));
        Assert.Equal("Failed", detail.Body.GetProperty("aiIndexStatus").GetString());

        // The AI service does not answer: 503 and the last known states stay.
        warmer.Answer = _ => null;
        var down = await AiSend(admin, AiRequest(HttpMethod.Post, "/api/admin/knowledge-sources/warm", null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.Status);
        Assert.Equal("AI_PROVIDER_UNAVAILABLE", down.Body.GetProperty("code").GetString());
        Assert.Equal("Ready", index.Of(good).Status);

        // Only an administrator may warm.
        using var anonymous = ai.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(AiRequest(HttpMethod.Post, "/api/admin/knowledge-sources/warm", null))).StatusCode);
    }

    [PostgresFact]
    public async Task Warm_all_is_unavailable_without_an_enabled_ai_adapter()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var warm = await AiSend(client, AiRequest(HttpMethod.Post, "/api/admin/knowledge-sources/warm", null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, warm.Status);
        Assert.Equal("AI_PROVIDER_UNAVAILABLE", warm.Body.GetProperty("code").GetString());
    }

    private static JsonElement Item(JsonElement items, Guid id) => items.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
}
