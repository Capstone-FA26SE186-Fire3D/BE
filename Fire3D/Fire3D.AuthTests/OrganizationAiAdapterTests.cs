using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>FastAPI adapter outcomes, provider result normalization and the worker loop, without a database or a real AI service.</summary>
public sealed class OrganizationAiAdapterTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request); return respond(request, ct); }
    }
    private sealed class CapturingLog : ILogger<OrganizationAiWorker>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) { lock (Entries) Entries.Add((level, format(state, error))); }
    }

    private static FastApiAiClient Client(Stub stub, int timeout = 5) =>
        new(stub, Options.Create(new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.fixture.test/adapter", ApiKey = "fixture-key", TimeoutSeconds = timeout }));
    private static JsonElement Claim(Guid id, string operation, string phase = "Dispatch") =>
        JsonSerializer.SerializeToElement(new { requestId = id, leaseToken = Guid.NewGuid(), phase, operation, payload = new { request_id = id } });
    private static HttpResponseMessage Status(int status, string? body = null, string media = "application/json") =>
        new((HttpStatusCode)status) { Content = new StringContent(body ?? "", Encoding.UTF8, media) };

    [Theory]
    [InlineData(401, AiCallStatus.NotDelivered)]
    [InlineData(403, AiCallStatus.NotDelivered)]
    [InlineData(404, AiCallStatus.NotDelivered)]
    [InlineData(429, AiCallStatus.NotDelivered)]
    [InlineData(503, AiCallStatus.NotDelivered)]
    [InlineData(500, AiCallStatus.Unknown)]
    [InlineData(502, AiCallStatus.Unknown)]
    public async Task Dispatch_status_codes_separate_undelivered_from_unknown_outcomes(int status, AiCallStatus expected)
    {
        var stub = new Stub((_, _) => Task.FromResult(Status(status, "{}")));
        var id = Guid.NewGuid();
        var result = await Client(stub).DispatchAsync(Claim(id, "TraineeAnswer"), default);
        Assert.Equal((expected, status.ToString()), (result.Status, result.Reason));
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/adapter/v2/trainee/answer", request.RequestUri!.AbsolutePath);
        Assert.Equal(id.ToString(), request.Headers.GetValues("X-Request-Id").Single());
        Assert.Equal(("Bearer", "fixture-key"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
    }

    [Fact]
    public async Task Refusals_keep_a_json_reason_and_tolerate_an_unreadable_body()
    {
        var refusal = await Client(new Stub((_, _) => Task.FromResult(Status(400, """{"code":"AI_INPUT_HASH_MISMATCH"}""")))).DispatchAsync(Claim(Guid.NewGuid(), "Answer"), default);
        Assert.Equal((AiCallStatus.Rejected, "400"), (refusal.Status, refusal.Reason));
        Assert.Equal("AI_INPUT_HASH_MISMATCH", refusal.Body!["code"]!.GetValue<string>());
        var broken = await Client(new Stub((_, _) => Task.FromResult(Status(422, "{not json")))).DispatchAsync(Claim(Guid.NewGuid(), "ScenarioDraft"), default);
        Assert.Equal((AiCallStatus.Rejected, "422", (JsonObject?)null), (broken.Status, broken.Reason, broken.Body));
        var text = await Client(new Stub((_, _) => Task.FromResult(Status(422, "refused", "text/plain")))).DispatchAsync(Claim(Guid.NewGuid(), "Answer"), default);
        Assert.Equal((AiCallStatus.Rejected, (JsonObject?)null), (text.Status, text.Body));
        // A completed call whose body is not JSON is still Completed; the gate then parks it as an invalid result.
        var garbled = await Client(new Stub((_, _) => Task.FromResult(Status(200, "{not json")))).LookupAsync(Guid.NewGuid(), default);
        Assert.Equal(AiCallStatus.Completed, garbled.Status); Assert.Empty(garbled.Body!);
    }

    [Fact]
    public async Task Lookup_distinguishes_missing_records_and_timeouts_and_honours_caller_cancellation()
    {
        var id = Guid.NewGuid();
        var missing = new Stub((_, _) => Task.FromResult(Status(404)));
        Assert.Equal(AiCallStatus.NotFound, (await Client(missing).LookupAsync(id, default)).Status);
        Assert.Equal($"/adapter/v2/requests/{id}", Assert.Single(missing.Requests).RequestUri!.AbsolutePath);
        Assert.Null(missing.Requests[0].Content);
        var failing = await Client(new Stub((_, _) => Task.FromResult(Status(500)))).LookupAsync(id, default);
        Assert.Equal((AiCallStatus.Unknown, "500"), (failing.Status, failing.Reason));
        var hanging = new Stub(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Status(200); });
        var timedOut = await Client(hanging, timeout: 0).DispatchAsync(Claim(id, "Answer"), default);
        Assert.Equal((AiCallStatus.Unknown, "timeout"), (timedOut.Status, timedOut.Reason));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(hanging).LookupAsync(id, cancelled.Token));
    }

    [Fact]
    public async Task A_scenario_draft_slower_than_the_answer_timeout_still_completes()
    {
        // #204: drafts ran past the 60 s answer timeout, ended Unknown and parked in NeedsReconcile. A draft now waits up to the
        // lease minus the settle margin; answers and lookups keep TimeoutSeconds (0 here, so they time out at once).
        var slow = new Stub(async (_, ct) => { await Task.Delay(200, ct); return Status(200, """{"request_id":"x"}"""); });
        var client = Client(slow, timeout: 0);
        Assert.Equal(AiCallStatus.Completed, (await client.DispatchAsync(Claim(Guid.NewGuid(), "ScenarioDraft"), default)).Status);
        Assert.Equal((AiCallStatus.Unknown, "timeout"), ((await client.DispatchAsync(Claim(Guid.NewGuid(), "Answer"), default)) is var a ? (a.Status, a.Reason) : default));
        Assert.Equal((AiCallStatus.Unknown, "timeout"), ((await client.LookupAsync(Guid.NewGuid(), default)) is var l ? (l.Status, l.Reason) : default));
        // An explicit draft timeout is honoured as well.
        var capped = new FastApiAiClient(slow, Options.Create(new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.fixture.test/adapter", ScenarioDraftTimeoutSeconds = 0 }));
        Assert.Equal((AiCallStatus.Unknown, "timeout"), ((await capped.DispatchAsync(Claim(Guid.NewGuid(), "ScenarioDraft"), default)) is var c ? (c.Status, c.Reason) : default));
    }

    [Theory]
    [InlineData(60, 180, null, 150)]   // defaults: lease 180 minus the 30 s margin
    [InlineData(180, 240, null, 210)]  // a longer answer timeout and lease
    [InlineData(60, 70, null, 60)]     // never below the answer timeout
    [InlineData(60, 900, null, 600)]   // capped at 600
    [InlineData(60, 180, 120, 120)]    // explicit value wins
    public void Scenario_draft_timeout_follows_the_lease_unless_set(int timeout, int lease, int? draft, int expected)
    {
        var o = new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.fixture.test/", TimeoutSeconds = timeout, LeaseSeconds = lease, ScenarioDraftTimeoutSeconds = draft };
        Assert.True(o.Configured);
        Assert.Equal((expected, timeout, timeout), (o.TimeoutFor("ScenarioDraft"), o.TimeoutFor("Answer"), o.TimeoutFor("TraineeAnswer")));
        Assert.True(o.TimeoutFor("ScenarioDraft") < o.LeaseSeconds);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    [InlineData(180)] // not below the 180 s lease: another worker could claim the request while the call still runs
    public void An_out_of_range_scenario_draft_timeout_turns_ai_off(int draft) =>
        Assert.False(new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.fixture.test/", ScenarioDraftTimeoutSeconds = draft }.Configured);

    private static JsonObject Answer(Guid id, Action<JsonObject>? change = null)
    {
        var body = new JsonObject { ["request_id"] = id.ToString(), ["response_type"] = "KnowledgeAnswer", ["status"] = "Answered", ["recommendation"] = "Use stair B.",
            ["citations"] = new JsonArray(new JsonObject { ["document_id"] = Guid.NewGuid().ToString(), ["document_version"] = Guid.NewGuid().ToString() }) };
        change?.Invoke(body); return body;
    }

    [Fact]
    public void Completion_keeps_valid_confidence_and_rule_results()
    {
        var id = Guid.NewGuid();
        var (completion, violation) = OrganizationAiInput.Completion("Answer", id, Answer(id, b =>
        {
            b["confidence"] = "high"; b["assumptions"] = new JsonArray("Doors are unlocked.");
            b["rule_results"] = new JsonArray(new JsonObject { ["rule_id"] = "QCVN06-3.2", ["outcome"] = "flag", ["reason"] = "Exit width below minimum." });
        }));
        Assert.Null(violation);
        var result = completion["result"]!;
        Assert.Equal("Answered", completion["status"]!.GetValue<string>());
        Assert.Equal("high", result["confidence"]!.GetValue<string>());
        Assert.Equal("Doors are unlocked.", result["assumptions"]![0]!.GetValue<string>());
        var rule = result["ruleResults"]![0]!;
        Assert.Equal(("QCVN06-3.2", "flag", "Exit width below minimum."), (rule["ruleId"]!.GetValue<string>(), rule["outcome"]!.GetValue<string>(), rule["reason"]!.GetValue<string>()));
        Assert.Null(completion["raw"]);
    }

    public static TheoryData<string, Action<JsonObject>> InvalidShapes => new()
    {
        { "confidence outside low/medium/high", b => b["confidence"] = "certain" },
        { "rule result with an unknown outcome", b => b["rule_results"] = new JsonArray(new JsonObject { ["rule_id"] = "R1", ["outcome"] = "maybe" }) },
        { "rule result without an id", b => b["rule_results"] = new JsonArray(new JsonObject { ["outcome"] = "pass" }) },
        { "rule results not a list", b => b["rule_results"] = "all good" },
        { "citations missing", b => b.Remove("citations") },
        { "citations not a list", b => b["citations"] = "QCVN 06" },
        { "assumption that is not text", b => b["assumptions"] = new JsonArray("ok", 5) },
        { "limitations not a list", b => b["limitations"] = "none" },
        { "BIM anchors not a list", b => b["bim_anchors"] = "IFC" }
    };

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public void Completion_parks_invalid_shapes_with_the_raw_body(string reason, Action<JsonObject> change)
    {
        var id = Guid.NewGuid();
        var (completion, violation) = OrganizationAiInput.Completion("Answer", id, Answer(id, change));
        Assert.True(violation == "AI_RESULT_INVALID", reason);
        Assert.Null(completion["status"]); Assert.Null(completion["result"]);
        Assert.Equal(id.ToString(), completion["raw"]!["request_id"]!.GetValue<string>());
    }

    private static (OrganizationAiWorker Worker, List<(string Action, string Input)> Calls) Worker(Func<string, int, CancellationToken, Task<JsonElement>> gate, IAiProviderClient? provider = null, ILogger<OrganizationAiWorker>? log = null, bool configured = true)
    {
        var calls = new List<(string Action, string Input)>();
        var gates = ResetProxy.For<IOrganizationAiGates>((method, args) =>
        {
            if (method != "WorkerAsync") throw new InvalidOperationException("Unexpected gate: " + method);
            int count; lock (calls) { calls.Add(((string)args[0]!, JsonSerializer.Serialize(args[4]))); count = calls.Count; }
            return gate((string)args[0]!, count, (CancellationToken)args[5]!);
        });
        var services = new ServiceCollection();
        services.AddSingleton(gates);
        services.AddSingleton(provider ?? ResetProxy.For<IAiProviderClient>((m, _) => throw new InvalidOperationException("Unexpected provider call: " + m)));
        var options = Options.Create(new OrganizationAiOptions { Enabled = configured, BaseUrl = "https://ai.fixture.test/", PollSeconds = 1, MaxAttempts = 2, BackoffSeconds = 1 });
        return (new OrganizationAiWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), options, log ?? NullLogger<OrganizationAiWorker>.Instance), calls);
    }
    private static JsonElement Gate(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Worker_turns_a_dispatch_refusal_into_a_reject_with_the_provider_code()
    {
        var id = Guid.NewGuid();
        var provider = ResetProxy.For<IAiProviderClient>((method, _) => method == "DispatchAsync"
            ? Task.FromResult(new AiCallResult(AiCallStatus.Rejected, new JsonObject { ["code"] = "AI_SOURCE_UNAVAILABLE" }, "422"))
            : throw new InvalidOperationException("Dispatch phase never looks up"));
        var (worker, calls) = Worker((action, _, _) => Task.FromResult(action == "Claim"
            ? Gate(new { code = "OK", result = Claim(id, "Answer") })
            : Gate(new { code = "OK", result = new { status = "Failed" } })), provider);
        Assert.True(await worker.RunOnceAsync(default));
        Assert.Equal(["Claim", "Reject"], calls.Select(x => x.Action));
        Assert.Equal("""{"code":"AI_SOURCE_UNAVAILABLE","message":null,"documentId":null,"httpStatus":"422","retryAfterSeconds":null}""", calls[1].Input);
        var withoutCode = ResetProxy.For<IAiProviderClient>((_, _) => Task.FromResult(new AiCallResult(AiCallStatus.Rejected, null, "400")));
        (worker, calls) = Worker((action, _, _) => Task.FromResult(action == "Claim" ? Gate(new { code = "OK", result = Claim(id, "Answer") }) : Gate(new { code = "AI_LEASE_LOST" })), withoutCode);
        Assert.True(await worker.RunOnceAsync(default));
        Assert.Equal("""{"code":null,"message":null,"documentId":null,"httpStatus":"400","retryAfterSeconds":null}""", calls[1].Input);
    }

    [Fact]
    public async Task Worker_loop_is_idle_without_an_adapter_and_survives_gate_failures_until_stopped()
    {
        var (idle, idleCalls) = Worker((_, _, _) => throw new InvalidOperationException("must not claim"), configured: false);
        await idle.StartAsync(default);
        await idle.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(idle.ExecuteTask.IsCompletedSuccessfully); Assert.Empty(idleCalls);

        var log = new CapturingLog(); var blocked = new TaskCompletionSource();
        var (worker, calls) = Worker(async (_, count, ct) =>
        {
            if (count == 1) throw new InvalidOperationException("database unavailable");
            if (count == 2) return Gate(new { code = "AI_NOTHING_TO_CLAIM" });
            blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Gate(new { code = "AI_NOTHING_TO_CLAIM" });
        }, log: log);
        await worker.StartAsync(default);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await worker.StopAsync(default);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(3, calls.Count); Assert.All(calls, x => Assert.Equal("Claim", x.Action));
        Assert.Contains(log.Entries, x => x.Level == LogLevel.Warning && x.Message == "Organization AI worker failed: InvalidOperationException");
    }
}
