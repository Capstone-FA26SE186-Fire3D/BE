using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>BE#187: sources still being prepared by the AI service, Retry-After, warming approved sources. No database, no network.</summary>
public sealed class AiSourcesPreparingTests
{
    private static readonly IOptions<OrganizationAiOptions> Configured = Options.Create(new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.fixture.test/adapter/", ApiKey = "fixture-key" });

    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(int status, string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(response);
        return response;
    }

    private static JsonElement Claim() => JsonSerializer.SerializeToElement(new { requestId = Guid.NewGuid(), operation = "TraineeAnswer", payload = new { request_id = "x" } });
    private static Task<AiCallResult> Dispatch(Stub stub) => new FastApiAiClient(stub, Configured).DispatchAsync(Claim(), default);

    private const string Preparing = """{"code":"AI_SOURCES_PREPARING","message":"2 source(s) are being prepared; retry in a few minutes"}""";

    // --- AI_SOURCES_PREPARING -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(503)]
    [InlineData(409)]
    public async Task Sources_still_being_prepared_are_a_rejection_with_the_default_wait_not_an_undelivered_call(int status)
    {
        var result = await Dispatch(new Stub((_, _) => Json(status, Preparing)));
        Assert.Equal(AiCallStatus.Rejected, result.Status);
        Assert.Equal(status.ToString(), result.Reason);
        Assert.Equal(AiRefusal.DefaultPreparingRetrySeconds, result.RetryAfterSeconds);
        Assert.Equal("AI_SOURCES_PREPARING", result.Body!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_Retry_After_header_replaces_the_default_wait()
    {
        var result = await Dispatch(new Stub((_, _) => Json(503, Preparing, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45)))));
        Assert.Equal(45, result.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(503, """{"code":"AI_TEMPORARILY_UNAVAILABLE","message":"x"}""", AiCallStatus.NotDelivered)]
    [InlineData(503, "{}", AiCallStatus.NotDelivered)]
    [InlineData(503, "not json", AiCallStatus.NotDelivered)]
    [InlineData(503, "", AiCallStatus.NotDelivered)]
    [InlineData(409, """{"code":"SOMETHING_ELSE"}""", AiCallStatus.Unknown)]
    [InlineData(409, "", AiCallStatus.Unknown)]
    public async Task Other_503_and_409_answers_keep_their_old_meaning(int status, string body, AiCallStatus expected)
    {
        var result = await Dispatch(new Stub((_, _) => Json(status, body)));
        Assert.Equal((expected, status.ToString(), (int?)null), (result.Status, result.Reason, result.RetryAfterSeconds));
    }

    [Fact]
    public async Task A_lookup_never_turns_the_preparing_answer_into_a_rejection()
    {
        var result = await new FastApiAiClient(new Stub((_, _) => Json(503, Preparing)), Configured).LookupAsync(Guid.NewGuid(), default);
        Assert.Equal(AiCallStatus.Unknown, result.Status);
    }

    // --- Retry-After ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_429_is_still_undelivered_and_carries_what_the_service_asked_for()
    {
        var asked = await Dispatch(new Stub((_, _) => Json(429, "{}", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)))));
        Assert.Equal((AiCallStatus.NotDelivered, "429", 30), (asked.Status, asked.Reason, asked.RetryAfterSeconds));
        var silent = await Dispatch(new Stub((_, _) => Json(429, "{}")));
        Assert.Equal((AiCallStatus.NotDelivered, "429", (int?)null), (silent.Status, silent.Reason, silent.RetryAfterSeconds));
    }

    [Theory]
    [InlineData(45, 45)]
    [InlineData(0, 1)]
    [InlineData(99999, 3600)]
    public void Retry_After_is_whole_seconds_between_1_and_3600(int delta, int expected) =>
        Assert.Equal(expected, AiRefusal.RetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(delta))));

    [Fact]
    public void Retry_After_may_be_a_date_and_may_be_absent()
    {
        var seconds = AiRefusal.RetryAfter(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(90)));
        Assert.InRange(seconds!.Value, 85, 91);
        Assert.Equal(1, AiRefusal.RetryAfter(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5))));
        Assert.Null(AiRefusal.RetryAfter(null));
    }

    [Fact]
    public void The_wait_travels_to_the_gate_with_the_refusal()
    {
        var input = JsonSerializer.Serialize(AiRefusal.Input(new JsonObject { ["code"] = "AI_SOURCES_PREPARING" }, "503", 120));
        Assert.Equal("""{"code":"AI_SOURCES_PREPARING","message":null,"documentId":null,"httpStatus":"503","retryAfterSeconds":120}""", input);
    }

    // --- warming ---------------------------------------------------------------------------------------------------------------------

    private static AiSourcePin Pin(int n = 1, Guid? id = null) => new(id ?? Guid.NewGuid(), "QCVN " + n, "v" + n, new string((char)('a' + n % 6), 64), "https://docs.example.test/" + n + ".pdf");

    private static string Accepted(params (Guid Id, string State, string? Code)[] states) =>
        JsonSerializer.Serialize(new { sources = states.Select(s => new { document_id = s.Id, state = s.State, code = s.Code }) });

    [Fact]
    public async Task Warming_posts_the_pins_of_a_dispatch_and_reads_the_states_back()
    {
        var a = Pin(1); var b = Pin(2); var c = Pin(3);
        var stub = new Stub((_, _) => Json(202, Accepted((a.SourceId, "ready", null), (b.SourceId, "queued", null), (c.SourceId, "failed", "AI_SOURCE_UNAVAILABLE"))));
        var states = await new FastApiSourceWarmer(stub, Configured, NullLogger<FastApiSourceWarmer>.Instance).WarmAsync([a, b, c], default);
        Assert.Equal([new(a.SourceId, "ready", null), new(b.SourceId, "queued", null), new(c.SourceId, "failed", "AI_SOURCE_UNAVAILABLE")], states);
        var (request, body) = Assert.Single(stub.Calls);
        Assert.Equal((HttpMethod.Post, "/adapter/v2/sources/warm"), (request.Method, request.RequestUri!.AbsolutePath));
        Assert.Equal(("Bearer", "fixture-key"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        var first = JsonNode.Parse(body!)!["sources"]![0]!;
        Assert.Equal("KnowledgeSource", first["kind"]!.GetValue<string>());
        Assert.Equal(a.SourceId.ToString(), first["document_id"]!.GetValue<string>());
        Assert.Equal(a.SourceId.ToString(), first["document_version"]!.GetValue<string>());
        Assert.Equal((a.Title, a.VersionLabel, a.ContentHash, a.SourceUri), (first["title"]!.GetValue<string>(), first["version_label"]!.GetValue<string>(), first["content_hash"]!.GetValue<string>(), first["source_uri"]!.GetValue<string>()));
        Assert.False(first["content_omitted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Answers_about_sources_that_were_not_asked_for_or_are_malformed_are_ignored()
    {
        var asked = Pin(1);
        var stub = new Stub((_, _) => Json(202, $$"""{"sources":[{"document_id":"{{Guid.NewGuid()}}","state":"ready"},{"document_id":"not-a-guid","state":"ready"},"text",{"state":"ready"},{"document_id":"{{asked.SourceId}}","state":"queued"}]}"""));
        var states = await new FastApiSourceWarmer(stub, Configured, NullLogger<FastApiSourceWarmer>.Instance).WarmAsync([asked], default);
        Assert.Equal([new AiSourceIndexState(asked.SourceId, "queued", null)], states);
    }

    [Fact]
    public async Task More_than_a_hundred_pins_go_out_in_batches_of_a_hundred()
    {
        var pins = Enumerable.Range(1, 150).Select(i => Pin(i)).ToList();
        var stub = new Stub((_, body) => Json(202, Accepted(JsonNode.Parse(body!)!["sources"]!.AsArray().Select(s => (Guid.Parse(s!["document_id"]!.GetValue<string>()), "ready", (string?)null)).ToArray())));
        var states = await new FastApiSourceWarmer(stub, Configured, NullLogger<FastApiSourceWarmer>.Instance).WarmAsync(pins, default);
        Assert.Equal(150, states!.Count);
        Assert.Equal([100, 50], stub.Calls.Select(c => JsonNode.Parse(c.Body!)!["sources"]!.AsArray().Count).ToArray());
    }

    [Fact]
    public async Task Any_failure_to_warm_is_null_and_never_an_exception()
    {
        var warm = (Stub s) => new FastApiSourceWarmer(s, Configured, NullLogger<FastApiSourceWarmer>.Instance).WarmAsync([Pin()], default);
        Assert.Null(await warm(new Stub((_, _) => Json(503, "{}"))));
        Assert.Null(await warm(new Stub((_, _) => Json(401, "{}"))));
        Assert.Null(await warm(new Stub((_, _) => Json(200, "{}"))));
        Assert.Null(await warm(new Stub((_, _) => Json(202, "{not json"))));
        Assert.Null(await warm(new Stub((_, _) => throw new HttpRequestException("down"))));
        Assert.Null(await warm(new Stub((_, _) => throw new TaskCanceledException())));
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stub = new Stub((_, _) => throw new OperationCanceledException(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FastApiSourceWarmer(stub, Configured, NullLogger<FastApiSourceWarmer>.Instance).WarmAsync([Pin()], cts.Token));
    }

    [Fact]
    public async Task Without_an_enabled_adapter_or_without_pins_nothing_is_sent()
    {
        var stub = new Stub((_, _) => Json(202, Accepted()));
        var off = new FastApiSourceWarmer(stub, Options.Create(new OrganizationAiOptions()), NullLogger<FastApiSourceWarmer>.Instance);
        Assert.Null(await off.WarmAsync([Pin()], default));
        Assert.Empty(await new FastApiSourceWarmer(stub, Configured, NullLogger<FastApiSourceWarmer>.Instance).WarmAsync([], default) ?? throw new Xunit.Sdk.XunitException("null"));
        Assert.Empty(stub.Calls);
    }

    // --- what Admin sees -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_index_shows_what_the_service_last_said_and_Unknown_before_that()
    {
        var index = new AiSourceIndex();
        var ready = Guid.NewGuid(); var queued = Guid.NewGuid(); var failed = Guid.NewGuid(); var odd = Guid.NewGuid();
        Assert.Equal(new AiIndexInfo("Unknown", null), index.Of(ready));
        index.Record([new(ready, "ready", null), new(queued, "queued", null), new(failed, "failed", "AI_SOURCE_HASH_MISMATCH"), new(odd, "weird", null)]);
        Assert.Equal(new AiIndexInfo("Ready", null), index.Of(ready));
        Assert.Equal(new AiIndexInfo("Preparing", null), index.Of(queued));
        Assert.Equal(new AiIndexInfo("Failed", "AI_SOURCE_HASH_MISMATCH"), index.Of(failed));
        Assert.Equal(new AiIndexInfo("Unknown", null), index.Of(odd));
        index.Record([new(failed, "ready", null)]);
        Assert.Equal(new AiIndexInfo("Ready", null), index.Of(failed));
    }

    [Fact]
    public void Enqueued_sources_wait_for_the_worker_in_order()
    {
        var index = new AiSourceIndex();
        var one = Pin(1); var two = Pin(2);
        index.Enqueue(one); index.Enqueue(two);
        Assert.True(index.Pending.TryRead(out var first)); Assert.Equal(one, first);
        Assert.True(index.Pending.TryRead(out var second)); Assert.Equal(two, second);
        Assert.False(index.Pending.TryRead(out _));
    }

    // --- the worker ------------------------------------------------------------------------------------------------------------------

    private sealed class Warmer(Func<int, Task<IReadOnlyList<AiSourceIndexState>?>> answer) : IAiSourceWarmer
    {
        public int Calls;
        public Task<IReadOnlyList<AiSourceIndexState>?> WarmAsync(IReadOnlyList<AiSourcePin> pins, CancellationToken ct) => answer(Calls++);
    }

    private static (KnowledgeSourceWarmWorker Worker, AiSourceIndex Index, List<TimeSpan> Waits) Worker(Warmer warmer)
    {
        var index = new AiSourceIndex(); var waits = new List<TimeSpan>();
        return (new KnowledgeSourceWarmWorker(index, warmer, Configured, NullLogger<KnowledgeSourceWarmWorker>.Instance, (d, _) => { waits.Add(d); return Task.CompletedTask; }), index, waits);
    }

    [Fact]
    public async Task The_worker_records_the_states_when_the_first_try_works()
    {
        var pin = Pin();
        var warmer = new Warmer(_ => Task.FromResult<IReadOnlyList<AiSourceIndexState>?>([new(pin.SourceId, "queued", null)]));
        var (worker, index, waits) = Worker(warmer);
        await worker.ProcessAsync([pin], default);
        Assert.Equal(1, warmer.Calls); Assert.Empty(waits);
        Assert.Equal("Preparing", index.Of(pin.SourceId).Status);
    }

    [Fact]
    public async Task The_worker_retries_with_growing_waits_until_the_service_answers()
    {
        var pin = Pin();
        var warmer = new Warmer(call => Task.FromResult<IReadOnlyList<AiSourceIndexState>?>(call < 2 ? null : [new(pin.SourceId, "ready", null)]));
        var (worker, index, waits) = Worker(warmer);
        await worker.ProcessAsync([pin], default);
        Assert.Equal(3, warmer.Calls);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)], waits);
        Assert.Equal("Ready", index.Of(pin.SourceId).Status);
    }

    [Fact]
    public async Task The_worker_gives_up_quietly_after_the_last_retry_and_treats_an_exception_like_no_answer()
    {
        var pin = Pin();
        var warmer = new Warmer(call => call % 2 == 0 ? Task.FromResult<IReadOnlyList<AiSourceIndexState>?>(null) : throw new InvalidOperationException("boom"));
        var (worker, index, waits) = Worker(warmer);
        await worker.ProcessAsync([pin], default);
        Assert.Equal(KnowledgeSourceWarmWorker.RetryDelays.Length + 1, warmer.Calls);
        Assert.Equal(KnowledgeSourceWarmWorker.RetryDelays, waits);
        Assert.Equal("Unknown", index.Of(pin.SourceId).Status);
    }

    [Fact]
    public async Task The_worker_stops_when_the_host_stops()
    {
        using var cts = new CancellationTokenSource();
        var warmer = new Warmer(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        var (worker, _, _) = Worker(warmer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessAsync([Pin()], cts.Token));
    }

    [Fact]
    public async Task The_running_worker_takes_everything_queued_in_one_batch_and_stays_idle_when_ai_is_off()
    {
        var one = Pin(1); var two = Pin(2);
        var seen = new List<int>();
        var warmer = new Warmer(_ => Task.FromResult<IReadOnlyList<AiSourceIndexState>?>([new(one.SourceId, "ready", null), new(two.SourceId, "failed", "AI_SOURCE_UNAVAILABLE")]));
        var counting = new CountingWarmer(warmer, seen);
        var index = new AiSourceIndex();
        index.Enqueue(one); index.Enqueue(two);
        var worker = new KnowledgeSourceWarmWorker(index, counting, Configured, NullLogger<KnowledgeSourceWarmWorker>.Instance);
        await worker.StartAsync(default);
        for (var i = 0; i < 100 && index.Of(two.SourceId).Status == "Unknown"; i++) await Task.Delay(50);
        await worker.StopAsync(default);
        Assert.Equal([2], seen);
        Assert.Equal("Failed", index.Of(two.SourceId).Status);

        var idle = new KnowledgeSourceWarmWorker(new AiSourceIndex(), counting, Options.Create(new OrganizationAiOptions()), NullLogger<KnowledgeSourceWarmWorker>.Instance);
        await idle.StartAsync(default);
        await idle.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(idle.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class CountingWarmer(IAiSourceWarmer inner, List<int> batches) : IAiSourceWarmer
    {
        public Task<IReadOnlyList<AiSourceIndexState>?> WarmAsync(IReadOnlyList<AiSourcePin> pins, CancellationToken ct) { batches.Add(pins.Count); return inner.WarmAsync(pins, ct); }
    }
}
