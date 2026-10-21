using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Submit routes need a configured adapter; no worker runs in the test host, so submitted requests stay Accepted.
    private WebApplicationFactory<Program> WithAiAdapter() => factory!.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OrganizationAi:Enabled"] = "true", ["OrganizationAi:BaseUrl"] = "https://ai.fixture.test/"
    })));
    private static HttpRequestMessage AiRequest(HttpMethod method, string url, object? body, string? key = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }
    private static async Task<(HttpStatusCode Status, JsonElement Body)> AiSend(HttpClient http, HttpRequestMessage request)
    {
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [PostgresFact]
    public async Task Ai_HTTP_policies_sources_submit_read_and_reconcile_through_the_gates()
    {
        using var ai = WithAiAdapter();
        using var admin = ai.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var policy = await admin.SendAsync(AiRequest(HttpMethod.Post, "/api/admin/ai/policies", new { operation = "Answer", quotaUnit = "tokens", reserveUnits = 100, maxInputChars = 1000, maxSources = 1 }, "policy-answer"));
        Assert.Equal(HttpStatusCode.Created, policy.StatusCode);
        Assert.Equal("/api/admin/ai/policies", policy.Headers.Location!.OriginalString);
        var daily = await AiSend(admin, AiRequest(HttpMethod.Post, "/api/admin/ai/policies", new { operation = "TraineeAnswer", quotaUnit = "tokens", reserveUnits = 100, maxInputChars = 1000, maxSources = 5, dailyUnits = 50 }, "policy-low"));
        Assert.Equal((HttpStatusCode)422, daily.Status);
        Assert.Contains(daily.Body.GetProperty("issues").EnumerateArray(), x => x.GetProperty("path").GetString() == "$.dailyUnits" && x.GetProperty("code").GetString() == "VALUE_OUT_OF_RANGE");
        Assert.Equal(HttpStatusCode.Created, (await AiSend(admin, AiRequest(HttpMethod.Post, "/api/admin/ai/policies", new { operation = "TraineeAnswer", quotaUnit = "tokens", reserveUnits = 100, maxInputChars = 1000, maxSources = 5, dailyUnits = 150 }, "policy-daily"))).Status);
        var policies = await admin.GetFromJsonAsync<JsonElement>("/api/admin/ai/policies?operation=Answer&pageSize=5");
        Assert.Equal(1, policies.GetProperty("total").GetInt32()); Assert.Equal(100, policies.GetProperty("items")[0].GetProperty("reserveUnits").GetInt32());

        var tenant = await SeedAiTenant(); await SeedAiGrant(tenant.Org, 1000);
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{tenant.User}'");
        using var org = ai.CreateClient(); org.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync($"ai-{tenant.User:N}@example.test")).AccessToken);
        object Answer(params Guid[] sources) => new { question = "Where is the safe exit?", sources = sources.Select(id => new { kind = "KnowledgeSource", id }).ToArray() };
        var noSources = await AiSend(org, AiRequest(HttpMethod.Post, "/api/ai/organization/answer", Answer(), "no-sources"));
        Assert.Equal("AI_NO_SOURCES", noSources.Body.GetProperty("code").GetString());
        Assert.Equal("No approved source is available for AI yet.", noSources.Body.GetProperty("title").GetString());

        async Task<Guid> Source(string label)
        {
            var created = await AiSend(admin, AiRequest(HttpMethod.Post, "/api/admin/knowledge-sources", new { visibility = "Common", title = "QCVN 06 " + label, versionLabel = label, sourceHash = Sha(label) }, "source-" + label));
            Assert.Equal(HttpStatusCode.Created, created.Status);
            var id = created.Body.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await AiSend(admin, AiRequest(HttpMethod.Post, $"/api/admin/knowledge-sources/{id}/approve", null, ifMatch: "\"1\""))).Status);
            return id;
        }
        var first = await Source("2022"); var second = await Source("2023");
        var duplicate = await AiSend(admin, AiRequest(HttpMethod.Post, "/api/admin/knowledge-sources", new { visibility = "Common", title = "QCVN 06 again", versionLabel = "2022", sourceHash = Sha("2022") }, "source-duplicate"));
        Assert.Equal(("KNOWLEDGE_SOURCE_EXISTS", "This source version is already registered."), (duplicate.Body.GetProperty("code").GetString(), duplicate.Body.GetProperty("title").GetString()));
        var approvedTwice = await AiSend(admin, AiRequest(HttpMethod.Post, $"/api/admin/knowledge-sources/{first}/approve", null, ifMatch: "\"2\""));
        Assert.Equal(("KNOWLEDGE_SOURCE_STATE_CONFLICT", "The source is not in a state that allows this action."), (approvedTwice.Body.GetProperty("code").GetString(), approvedTwice.Body.GetProperty("title").GetString()));
        var tooMany = await AiSend(org, AiRequest(HttpMethod.Post, "/api/ai/organization/answer", Answer(first, second), "too-many"));
        Assert.Equal(("AI_TOO_MANY_SOURCES", "Too many sources for the active policy."), (tooMany.Body.GetProperty("code").GetString(), tooMany.Body.GetProperty("title").GetString()));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM ai_requests"));

        using var accepted = await org.SendAsync(AiRequest(HttpMethod.Post, "/api/ai/organization/answer", Answer(first), "answer"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetGuid();
        Assert.Equal($"/api/ai/requests/{id}", accepted.Headers.Location!.OriginalString);
        Assert.Equal("Accepted", (await org.GetFromJsonAsync<JsonElement>(accepted.Headers.Location)).GetProperty("status").GetString());
        var mine = await org.GetFromJsonAsync<JsonElement>("/api/ai/requests?status=Accepted&mine=true&page=1&pageSize=10");
        Assert.Equal(1, mine.GetProperty("total").GetInt32()); Assert.Equal(id, mine.GetProperty("items")[0].GetProperty("requestId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await org.GetAsync("/api/ai/requests?page=0")).StatusCode);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/ai/requests/{id}");
        Assert.Equal((tenant.User, tenant.Org), (detail.GetProperty("actorUserId").GetGuid(), detail.GetProperty("organizationId").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/ai/requests/{Guid.NewGuid()}")).StatusCode);
        var early = await AiSend(admin, AiRequest(HttpMethod.Post, $"/api/admin/ai/requests/{id}/reconcile", null, "reconcile-early"));
        Assert.Equal((HttpStatusCode.Conflict, "AI_RECONCILE_NOT_ALLOWED"), (early.Status, early.Body.GetProperty("code").GetString()));
        // The provider commits the work but the response is lost: the request waits for an Admin reconcile.
        await WithAiRuntime(async runtime =>
        {
            var fake = new FakeFastApi { Respond = (_, _) => throw new HttpRequestException(HttpRequestError.ResponseEnded, "dropped") };
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
        });
        using var reconciled = await admin.SendAsync(AiRequest(HttpMethod.Post, $"/api/admin/ai/requests/{id}/reconcile", null, "reconcile"));
        Assert.Equal(HttpStatusCode.Accepted, reconciled.StatusCode);
        Assert.Equal($"/api/admin/ai/requests/{id}", reconciled.Headers.Location!.OriginalString);
        Assert.NotNull(await ScalarAsync($"SELECT reconcile_requested_at FROM ai_requests WHERE id='{id}'"));

        var (trainee, _) = await SeedTrainee();
        using var learner = ai.CreateClient(); learner.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync($"trainee-{trainee:N}@example.test")).AccessToken);
        foreach (var context in new object[] { new { kind = "Learn", postId = Guid.NewGuid() }, new { kind = "Result" }, new { kind = "Building", trainingId = Guid.NewGuid() } })
        {
            var invalid = await AiSend(learner, AiRequest(HttpMethod.Post, "/api/ai/trainee/answer", new { question = "Why stairs?", context }, "trainee-invalid"));
            Assert.Equal((HttpStatusCode)422, invalid.Status);
            Assert.Contains(invalid.Body.GetProperty("issues").EnumerateArray(), x => x.GetProperty("path").GetString() == "$.context");
        }
        using var asked = await learner.SendAsync(AiRequest(HttpMethod.Post, "/api/ai/trainee/answer", new { question = "Why use the stairs?" }, "trainee"));
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var traineeRequest = (await asked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requestId").GetGuid();
        Assert.Equal($"/api/ai/trainee/requests/{traineeRequest}", asked.Headers.Location!.OriginalString);
        var own = await learner.GetFromJsonAsync<JsonElement>(asked.Headers.Location);
        Assert.Equal(("trainee", "Accepted"), (own.GetProperty("audience").GetString(), own.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await learner.GetAsync($"/api/ai/trainee/requests/{id}")).StatusCode);
    }
}
