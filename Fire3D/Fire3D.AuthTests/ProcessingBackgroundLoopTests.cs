using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Content;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Content;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>
/// Hosted-service loops of the processing pipeline with in-memory gates and streams: disabled services never touch their
/// dependencies, a failed run is logged and retried on the next tick, and shutdown during a run stops the loop cleanly.
/// </summary>
public sealed class ProcessingBackgroundLoopTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Web);
    private static T Untouched<T>() where T : class => ResetProxy.For<T>((method, _) => throw new Exception("Disabled service touched " + typeof(T).Name + "." + method));
    private static IServiceScopeFactory Scopes(Action<ServiceCollection> register)
    {
        var services = new ServiceCollection(); register(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
    private static ProcessingEnvelope Envelope(Guid? job = null)
    {
        var id = job ?? Guid.NewGuid();
        return new("processing:" + id.ToString("N"), "ProcessingJobRequested", "1", "ProcessingJob", id, Guid.NewGuid(), Json(new { job_id = id }), new string('c', 64));
    }

    /// <summary>First call fails, the second blocks until shutdown; returns when the second call has started.</summary>
    private sealed class Script
    {
        private int calls;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => calls;
        public async Task<T> Next<T>(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("Simulated dependency outage");
            Blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static async Task AssertDisabled(BackgroundService service)
    {
        await service.StartAsync(default);
        // .NET 10 starts ExecuteAsync on a background thread; a disabled service finishes at once instead of polling.
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        await service.StopAsync(default);
    }

    private static async Task AssertRetriesThenStops<T>(BackgroundService service, Script script, ListLogger<T> log, string warning)
    {
        await service.StartAsync(default);
        try { await script.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { Assert.Fail($"Calls={script.Calls}; Task={service.ExecuteTask?.Status} {service.ExecuteTask?.Exception}; Log={string.Join(" | ", log.Messages)}"); }
        await service.StopAsync(default);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(2, script.Calls);
        Assert.Contains(log.Messages, m => m.Level == LogLevel.Warning && m.Text == warning);
    }

    [Fact]
    public async Task Disabled_or_other_transport_processing_services_return_without_touching_dependencies()
    {
        var off = Options.Create(new ProcessingWorkerOptions { Transport = "RedisStreams" });
        var http = Options.Create(new ProcessingWorkerOptions { Transport = "Http", DispatcherEnabled = true, ConsumerEnabled = true });
        var redis = Options.Create(new RedisProcessingOptions());
        foreach (var options in new[] { off, http })
        {
            await AssertDisabled(new RedisProcessingConsumer(Untouched<IServiceScopeFactory>(), Untouched<IProcessingStream>(), Untouched<IHttpClientFactory>(), options, new ListLogger<RedisProcessingConsumer>()));
            await AssertDisabled(new RedisProcessingPublisher(Untouched<IServiceScopeFactory>(), Untouched<IProcessingStream>(), options, new ListLogger<RedisProcessingPublisher>()));
            await AssertDisabled(new RedisProcessingRecovery(Untouched<IServiceScopeFactory>(), Untouched<IProcessingStream>(), options, redis, new ListLogger<RedisProcessingRecovery>()));
        }
        foreach (var options in new[] { Options.Create(new ProcessingWorkerOptions { Transport = "Http" }), Options.Create(new ProcessingWorkerOptions { Transport = "RedisStreams", DispatcherEnabled = true }) })
            await AssertDisabled(new ProcessingDispatcher(Untouched<IServiceScopeFactory>(), Untouched<IHttpClientFactory>(), options, new ListLogger<ProcessingDispatcher>()));
        await AssertDisabled(new IfcUploadCleanupWorker(Untouched<IServiceScopeFactory>(), Options.Create(new IfcUploadOptions { CleanupEnabled = false }), new ListLogger<IfcUploadCleanupWorker>()));
        var cache = new ListLogger<PlatformCacheInvalidationWorker>();
        await AssertDisabled(new PlatformCacheInvalidationWorker(new ConfigurationBuilder().Build(), Untouched<IPlatformCacheVersions>(), Options.Create(new PlatformCacheOptions { WorkerEnabled = false }), cache));
        Assert.Contains(cache.Messages, m => m.Text == "Platform cache invalidation worker is disabled.");
    }

    [Fact]
    public async Task Redis_consumer_loop_logs_an_unavailable_stream_and_stops_during_the_next_read()
    {
        var script = new Script();
        var stream = ResetProxy.For<IProcessingStream>((method, args) => method switch
        {
            nameof(IProcessingStream.ReadAsync) => Task.FromResult<IReadOnlyList<ProcessingStreamMessage>>([]),
            nameof(IProcessingStream.ReclaimAsync) => script.Next<IReadOnlyList<ProcessingStreamMessage>>((CancellationToken)args[1]!),
            _ => throw new Exception("Unexpected: " + method)
        });
        var log = new ListLogger<RedisProcessingConsumer>();
        var consumer = new RedisProcessingConsumer(Untouched<IServiceScopeFactory>(), stream, Untouched<IHttpClientFactory>(),
            Options.Create(new ProcessingWorkerOptions { Transport = "RedisStreams", ConsumerEnabled = true, PollSeconds = 1 }), log);
        await AssertRetriesThenStops(consumer, script, log, "Redis consumer unavailable: IOException");
    }

    [Fact]
    public async Task Redis_publisher_loop_logs_an_unavailable_gate_and_stops_during_the_next_claim()
    {
        var script = new Script();
        var gate = ResetProxy.For<IProcessingStreamDispatchGate>((_, args) => script.Next<JsonElement>((CancellationToken)args[2]!));
        var log = new ListLogger<RedisProcessingPublisher>();
        var publisher = new RedisProcessingPublisher(Scopes(s => s.AddScoped(_ => gate)), new MemoryProcessingStream(),
            Options.Create(new ProcessingWorkerOptions { Transport = "RedisStreams", DispatcherEnabled = true, PollSeconds = 1 }), log);
        await AssertRetriesThenStops(publisher, script, log, "Redis publisher unavailable: IOException");
    }

    [Fact]
    public async Task Redis_recovery_loop_runs_for_the_consumer_alone_and_logs_an_unavailable_gate()
    {
        var script = new Script();
        var gate = ResetProxy.For<IProcessingStreamRecoveryGate>((_, args) => script.Next<JsonElement>((CancellationToken)args[2]!));
        var log = new ListLogger<RedisProcessingRecovery>();
        var recovery = new RedisProcessingRecovery(Scopes(s => s.AddScoped(_ => gate)), Untouched<IProcessingStream>(),
            Options.Create(new ProcessingWorkerOptions { Transport = "RedisStreams", ConsumerEnabled = true }), Options.Create(new RedisProcessingOptions { RecoverySeconds = 1 }), log);
        await AssertRetriesThenStops(recovery, script, log, "Processing Redis recovery unavailable: IOException");
    }

    [Fact]
    public async Task Http_dispatcher_loop_logs_a_failed_recovery_and_stops_during_the_next_run()
    {
        var script = new Script();
        var gate = ResetProxy.For<IProcessingDispatchGate>((_, args) => script.Next<JsonElement>((CancellationToken)args[4]!));
        var log = new ListLogger<ProcessingDispatcher>();
        var dispatcher = new ProcessingDispatcher(Scopes(s => s.AddScoped(_ => gate)), Untouched<IHttpClientFactory>(),
            Options.Create(new ProcessingWorkerOptions { DispatcherEnabled = true, PollSeconds = 1 }), log);
        await AssertRetriesThenStops(dispatcher, script, log, "Processing dispatcher failed: IOException");
    }

    [Fact]
    public async Task Ifc_cleanup_loop_logs_a_failed_run_and_keeps_polling_until_shutdown()
    {
        var log = new ListLogger<IfcUploadCleanupWorker>();
        // No DbContext is registered: every run fails before touching storage.
        var worker = new IfcUploadCleanupWorker(Scopes(_ => { }), Options.Create(new IfcUploadOptions { CleanupEnabled = true }), log);
        await worker.StartAsync(default);
        await log.Logged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);
        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.Contains(log.Messages, m => m.Level == LogLevel.Error && m.Text == "IFC cleanup failed. ErrorType=InvalidOperationException");
    }

    [Fact]
    public async Task Platform_cache_loop_logs_a_failed_run_and_stops_while_waiting_for_the_next_tick()
    {
        var log = new ListLogger<PlatformCacheInvalidationWorker>();
        // No connection string: each run fails before any database or cache call.
        var worker = new PlatformCacheInvalidationWorker(new ConfigurationBuilder().Build(), Untouched<IPlatformCacheVersions>(), Options.Create(new PlatformCacheOptions { PollSeconds = 30 }), log);
        await worker.StartAsync(default);
        await log.Logged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Contains(log.Messages, m => m.Level == LogLevel.Error && m.Text == "Platform cache invalidation run failed.");
    }

    [Fact]
    public async Task Redis_recovery_trims_only_valid_envelopes_the_gate_marks_safe_and_reports_blocked_events()
    {
        var safe = Envelope(); var protectedEnvelope = Envelope();
        var invalid = Envelope() with { PayloadHash = "not-a-hash" };
        var messages = new ProcessingStreamMessage[]
        {
            new("1-0", "{broken"), new("2-0", JsonSerializer.Serialize(invalid, Web)),
            new("3-0", JsonSerializer.Serialize(safe, Web)), new("4-0", JsonSerializer.Serialize(protectedEnvelope, Web))
        };
        var calls = new List<(string Action, JsonElement Input)>();
        var gate = ResetProxy.For<IProcessingStreamRecoveryGate>((_, args) =>
        {
            var (action, input) = ((string)args[0]!, (JsonElement)args[1]!);
            lock (calls) calls.Add((action, input));
            return Task.FromResult(action switch
            {
                "RecoverAttempt" => Json(new { code = "OK" }),
                "ReplayMissing" => Json(new { code = "OK", replayed = 2, blocked = 3L }),
                "CanTrim" => Json(new { code = input.GetProperty("envelope").GetProperty("eventKey").GetString() == safe.EventKey ? "SAFE" : "PROTECTED" }),
                _ => throw new Exception("Unexpected: " + action)
            });
        });
        var trimmed = new List<string>();
        var stream = ResetProxy.For<IProcessingStream>((method, args) => method switch
        {
            "get_StreamName" => "fet3d:test:processing",
            nameof(IProcessingStream.TrimAsync) => Trim((Func<ProcessingStreamMessage, CancellationToken, Task<bool>>)args[0]!),
            nameof(IProcessingStream.BacklogAsync) => Task.FromResult(new ProcessingStreamBacklog(4 - trimmed.Count, 1)),
            _ => throw new Exception("Unexpected: " + method)
        });
        async Task<int> Trim(Func<ProcessingStreamMessage, CancellationToken, Task<bool>> handedOff)
        {
            foreach (var message in messages) if (await handedOff(message, default)) trimmed.Add(message.Id);
            return trimmed.Count;
        }
        var log = new ListLogger<RedisProcessingRecovery>();
        var recovery = new RedisProcessingRecovery(Scopes(s => s.AddScoped(_ => gate)), stream,
            Options.Create(new ProcessingWorkerOptions { Transport = "RedisStreams", ConsumerEnabled = true }), Options.Create(new RedisProcessingOptions { HandoffTimeoutSeconds = 120 }), log);
        await recovery.RunOnceAsync(default);

        Assert.Equal(["3-0"], trimmed);
        Assert.Equal(["RecoverAttempt", "ReplayMissing", "CanTrim", "CanTrim"], calls.Select(x => x.Action));
        var replay = calls[1].Input;
        Assert.Equal(("fet3d:test:processing", 120), (replay.GetProperty("streamName").GetString(), replay.GetProperty("handoffTimeoutSeconds").GetInt32()));
        var canTrim = calls[2].Input;
        Assert.Equal(("fet3d:test:processing", "3-0"), (canTrim.GetProperty("streamName").GetString(), canTrim.GetProperty("streamId").GetString()));
        Assert.Contains(log.Messages, m => m.Level == LogLevel.Warning && m.Text.StartsWith("Processing Redis delivery retries exhausted: BlockedEvents=3."));
        Assert.Contains(log.Messages, m => m.Level == LogLevel.Information && m.Text == "Processing Redis recovery: Replayed=2, Trimmed=1, StreamLength=3, Pending=1");
    }

    [Fact]
    public async Task Shutdown_during_a_handoff_or_publication_propagates_without_failing_the_delivery()
    {
        using var stop = new CancellationTokenSource();
        var actions = new List<string>();
        var envelope = Envelope();
        HttpClient Hanging() => new(new FakeProcessingHttp(async request =>
        {
            await stop.CancelAsync();
            await Task.Delay(Timeout.Infinite, stop.Token);
            return new HttpResponseMessage();
        }));

        // Consumer: shutdown while the worker call is in flight leaves the message pending for reclaim; no Failure is recorded.
        var consumerGate = ResetProxy.For<IProcessingStreamConsumerGate>((_, args) => { actions.Add((string)args[0]!); return Task.FromResult(Json(new { code = "OK" })); });
        var consumer = new RedisProcessingConsumer(Scopes(s => s.AddScoped(_ => consumerGate)), new MemoryProcessingStream(),
            ResetProxy.For<IHttpClientFactory>((_, _) => Hanging()), Options.Create(new ProcessingWorkerOptions { WorkerUrl = "https://worker.example.test/deliver", MachineKey = new string('k', 32) }),
            new ListLogger<RedisProcessingConsumer>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.HandleAsync(new("1-0", JsonSerializer.Serialize(envelope, Web)), stop.Token));
        Assert.Equal(["Inspect"], actions);

        // Publisher: shutdown during XADD keeps the lease for recovery instead of recording a failed publication.
        using var publishing = new CancellationTokenSource();
        actions.Clear();
        var dispatchGate = ResetProxy.For<IProcessingStreamDispatchGate>((_, args) =>
        {
            actions.Add((string)args[0]!);
            return Task.FromResult(Json(new { code = "OK", envelope, leaseToken = Guid.NewGuid() }));
        });
        var stream = ResetProxy.For<IProcessingStream>((method, args) => method switch
        {
            "get_StreamName" => "fet3d:test:processing",
            nameof(IProcessingStream.PublishAsync) => Cancelled(publishing, (CancellationToken)args[1]!),
            _ => throw new Exception("Unexpected: " + method)
        });
        var publisher = new RedisProcessingPublisher(Scopes(s => s.AddScoped(_ => dispatchGate)), stream, Options.Create(new ProcessingWorkerOptions()), new ListLogger<RedisProcessingPublisher>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.RunOnceAsync(publishing.Token));
        Assert.Equal(["Claim"], actions);

        // HTTP dispatcher: shutdown during delivery does not call Fail; recovery expires the lease later.
        using var dispatching = new CancellationTokenSource();
        actions.Clear();
        var key = "processing:" + Guid.NewGuid().ToString("N");
        var dispatchGates = ResetProxy.For<IProcessingDispatchGate>((_, args) =>
        {
            actions.Add((string)args[0]!);
            return Task.FromResult((string)args[0]! == "Claim"
                ? Json(new { code = "OK", eventKey = key, leaseToken = Guid.NewGuid(), payloadHash = new string('c', 64), attempt = 1 })
                : Json(new { code = "OK" }));
        });
        var dispatcher = new ProcessingDispatcher(Scopes(s => s.AddScoped(_ => dispatchGates)),
            ResetProxy.For<IHttpClientFactory>((_, _) => new HttpClient(new FakeProcessingHttp(async _ => { await dispatching.CancelAsync(); await Task.Delay(Timeout.Infinite, dispatching.Token); return new HttpResponseMessage(); }))),
            Options.Create(new ProcessingWorkerOptions { DispatcherEnabled = true, WorkerUrl = "https://worker.example.test/jobs", MachineKey = new string('k', 32) }), new ListLogger<ProcessingDispatcher>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.RunOnceAsync(dispatching.Token));
        Assert.Equal(["Recover", "Claim"], actions);
    }

    private static async Task<string> Cancelled(CancellationTokenSource source, CancellationToken ct)
    {
        await source.CancelAsync();
        await Task.Delay(Timeout.Infinite, ct);
        return "unreachable";
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Text)> Messages { get; } = [];
    public TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages) Messages.Add((logLevel, formatter(state, exception)));
        if (logLevel >= LogLevel.Warning) Logged.TrySetResult();
    }
}
