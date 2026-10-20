using Fire3D.API.Extensions;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class ProcessingReadinessTests
{
    [Theory]
    [InlineData("Http", true, false)]
    [InlineData("RedisStreams", false, false)]
    public async Task Inactive_processing_does_not_probe_dependencies(string transport, bool dispatcher, bool consumer)
    {
        var probe = new Probe { Failure = new InvalidOperationException("Dependencies must not be contacted") };
        var result = await Check(probe, transport, dispatcher, consumer);
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("PROCESSING_DISABLED", result.Description);
        Assert.Empty(probe.Gates);
    }

    [Fact]
    public async Task Redis_outage_fails_readiness()
    {
        var result = await Check(new Probe { RedisConnected = false });
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("PROCESSING_REDIS_UNAVAILABLE", result.Description);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Healthy_processing_checks_only_enabled_gates_and_always_recovery(bool dispatcher, bool consumer)
    {
        var probe = new Probe();
        var result = await Check(probe, dispatcher: dispatcher, consumer: consumer);
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("OK", result.Description);
        Assert.Equal(dispatcher, probe.Gates.Contains(("DispatcherExecutor", "processing_stream_dispatch_gate(text,jsonb)")));
        Assert.Equal(consumer, probe.Gates.Contains(("DefaultConnection", "processing_stream_consumer_gate(text,jsonb)")));
        Assert.Contains(("DefaultConnection", "processing_stream_recovery_gate(text,jsonb)"), probe.Gates);
        Assert.Equal(1 + (dispatcher ? 1 : 0) + (consumer ? 1 : 0), probe.Gates.Count);
    }

    [Theory]
    [InlineData("processing_stream_dispatch_gate(text,jsonb)", "PROCESSING_DISPATCHER_FORBIDDEN")]
    [InlineData("processing_stream_consumer_gate(text,jsonb)", "PROCESSING_CONSUMER_FORBIDDEN")]
    [InlineData("processing_stream_recovery_gate(text,jsonb)", "PROCESSING_RECOVERY_FORBIDDEN")]
    public async Task Missing_gate_privileges_fail_readiness(string gate, string code)
    {
        var result = await Check(new Probe { ForbiddenGate = gate });
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(code, result.Description);
    }

    [Theory]
    [InlineData(null, "PROCESSING_REDIS_UNAVAILABLE")]
    [InlineData("processing_stream_dispatch_gate(text,jsonb)", "PROCESSING_DISPATCHER_UNAVAILABLE")]
    [InlineData("processing_stream_consumer_gate(text,jsonb)", "PROCESSING_CONSUMER_UNAVAILABLE")]
    [InlineData("processing_stream_recovery_gate(text,jsonb)", "PROCESSING_RECOVERY_UNAVAILABLE")]
    public async Task Dependency_exceptions_are_sanitized(string? failingGate, string code)
    {
        var result = await Check(new Probe
        {
            Failure = new InvalidOperationException("Host=private.example;Password=secret"),
            FailingGate = failingGate
        });
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(code, result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var check = new ProcessingReadinessCheck(Options.Create(new ProcessingWorkerOptions
            { Transport = "RedisStreams", DispatcherEnabled = true }), new Probe());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.CheckHealthAsync(new(), cancellation.Token));
    }

    [RedisFact]
    public async Task Redis_probe_does_not_create_stream_and_detects_wrong_key_type()
    {
        var options = RedisStreamIntegrationTests.TestOptions();
        await using var stream = new RedisProcessingStream(Options.Create(options),
            RedisStreamIntegrationTests.TestEnvironment("readiness-" + Guid.NewGuid().ToString("N")));
        await using var connection = await RedisStreamIntegrationTests.ConnectAsync(options);
        var db = connection.GetDatabase();
        try
        {
            Assert.True(await stream.ConnectedAsync(default));
            Assert.False(await db.KeyExistsAsync(stream.StreamName));
            await db.StringSetAsync(stream.StreamName, "invalid-stream-type");
            Assert.False(await stream.ConnectedAsync(default));
            Assert.Equal("invalid-stream-type", (string?)await db.StringGetAsync(stream.StreamName));
        }
        finally { await db.KeyDeleteAsync(stream.StreamName); }
    }

    private static Task<HealthCheckResult> Check(Probe probe, string transport = "RedisStreams", bool dispatcher = true, bool consumer = true) =>
        new ProcessingReadinessCheck(Options.Create(new ProcessingWorkerOptions
            { Transport = transport, DispatcherEnabled = dispatcher, ConsumerEnabled = consumer }), probe)
            .CheckHealthAsync(new());

    private sealed class Probe : IProcessingReadinessProbe
    {
        public bool RedisConnected { get; init; } = true;
        public string? ForbiddenGate { get; init; }
        public string? FailingGate { get; init; }
        public Exception? Failure { get; init; }
        public List<(string Connection, string Gate)> Gates { get; } = [];

        public Task<bool> RedisConnectedAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure is not null && FailingGate is null) throw Failure;
            return Task.FromResult(RedisConnected);
        }

        public Task<bool> CanExecuteGateAsync(string connectionName, string gate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Gates.Add((connectionName, gate));
            if (Failure is not null && FailingGate == gate) throw Failure;
            return Task.FromResult(ForbiddenGate != gate);
        }
    }
}

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Processing_readiness_uses_restricted_logins_and_read_only_catalog_checks()
    {
        await WithRedisRuntime(async configuration =>
        {
            var values = new Dictionary<string, string?>();
            foreach (var name in new[] { "DefaultConnection", "DispatcherExecutor" })
            {
                var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(name)!)
                    { Options = "-c default_transaction_read_only=on" };
                values["ConnectionStrings:" + name] = connection.ConnectionString;
            }
            var readOnlyConfiguration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var probe = new ProcessingReadinessProbe(new MemoryProcessingStream(), readOnlyConfiguration);
            var check = new ProcessingReadinessCheck(Options.Create(new ProcessingWorkerOptions
                { Transport = "RedisStreams", DispatcherEnabled = true, ConsumerEnabled = true }), probe);

            var result = await check.CheckHealthAsync(new());
            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Equal("OK", result.Description);
            Assert.False(await probe.CanExecuteGateAsync("DefaultConnection", "processing_stream_dispatch_gate(text,jsonb)", default));
            Assert.False(await probe.CanExecuteGateAsync("DefaultConnection", "missing_processing_gate(text,jsonb)", default));

            var privileged = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:DefaultConnection"] = testConnection }).Build();
            Assert.False(await new ProcessingReadinessProbe(new MemoryProcessingStream(), privileged)
                .CanExecuteGateAsync("DefaultConnection", "processing_stream_recovery_gate(text,jsonb)", default));
        });
    }
}
