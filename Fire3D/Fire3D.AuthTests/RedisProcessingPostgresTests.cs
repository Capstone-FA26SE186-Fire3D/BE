using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task WithRedisRuntime(Func<IConfiguration, Task> action)
    {
        await WithProcessingRuntime(async (api, worker) =>
        {
            var dispatcher = "redis_dispatch_test_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync($"CREATE ROLE {dispatcher} LOGIN NOSUPERUSER NOBYPASSRLS; GRANT USAGE ON SCHEMA public TO {dispatcher}; GRANT fet3d_dispatcher_executor TO {dispatcher}");
            try
            {
                var dispatch = new NpgsqlConnectionStringBuilder(testConnection) { Username = dispatcher, Password = "", Pooling = false }.ConnectionString;
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = api,
                    ["ConnectionStrings:DispatcherExecutor"] = dispatch,
                    ["ConnectionStrings:ProcessingExecutor"] = worker
                }).Build();
                Assert.Equal(false, await ScalarAsync($"SELECT has_table_privilege('{dispatcher}','processing_jobs','UPDATE')"));
                Assert.Equal(false, await ScalarAsync($"SELECT has_table_privilege('{dispatcher}','integration_outbox_events','UPDATE')"));
                Assert.Equal(false, await ScalarAsync($"SELECT has_function_privilege('{dispatcher}','processing_worker_gate(text,uuid,jsonb)','EXECUTE')"));
                await action(config);
            }
            finally { await ExecuteAsync($"DROP OWNED BY {dispatcher}; DROP ROLE {dispatcher}"); }
        });
    }

    private static ServiceProvider RedisScope(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddScoped<IProcessingStreamDispatchGate>(_ => new ProcessingStreamStore(config));
        services.AddScoped<IProcessingStreamConsumerGate>(_ => new ProcessingStreamStore(config));
        return services.BuildServiceProvider();
    }

    [PostgresFact]
    public async Task Redis_publish_is_leased_atomic_and_does_not_require_worker_receipt()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var context = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var job = (await new ProcessingRuntimeStore(context, config).RequestAsync(adminId, revision, "redis-publish", default)).Value;
            using var scopes = RedisScope(config);
            var stream = new MemoryProcessingStream();
            var publisher = new RedisProcessingPublisher(scopes.GetRequiredService<IServiceScopeFactory>(), stream, Options.Create(new ProcessingWorkerOptions()), NullLogger<RedisProcessingPublisher>.Instance);
            var published = await Task.WhenAll(publisher.RunOnceAsync(default), publisher.RunOnceAsync(default));
            Assert.Single(published, x => x); Assert.Single(stream.Published);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM processing_stream_deliveries d JOIN integration_outbox_events e ON e.idempotency_key=d.event_key WHERE e.aggregate_id='{job}' AND e.status='Published'"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM processing_delivery_receipts"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM processing_job_attempts"));
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM organizations WHERE id=(SELECT organization_id FROM revisions WHERE id='{revision}')"));
        });
    }

    [PostgresFact]
    public async Task Redis_lost_XADD_response_replays_same_envelope_and_stale_lease_cannot_publish()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var context = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var job = (await new ProcessingRuntimeStore(context, config).RequestAsync(adminId, revision, "redis-lost", default)).Value;
            using var scopes = RedisScope(config);
            var stream = new MemoryProcessingStream { LoseResponse = true };
            var publisher = new RedisProcessingPublisher(scopes.GetRequiredService<IServiceScopeFactory>(), stream, Options.Create(new ProcessingWorkerOptions()), NullLogger<RedisProcessingPublisher>.Instance);
            Assert.False(await publisher.RunOnceAsync(default));
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM integration_outbox_events WHERE aggregate_id='{job}' AND status='Pending' AND last_error='REDIS_PUBLICATION_UNAVAILABLE'"));
            await ExecuteAsync("UPDATE integration_outbox_events SET available_at=now()-interval '1 second'");
            stream.LoseResponse = false;
            Assert.True(await publisher.RunOnceAsync(default));
            Assert.Equal(2, stream.Published.Count);
            Assert.Equal(stream.Published[0].EventKey, stream.Published[1].EventKey);
            Assert.Equal(stream.Published[0].PayloadHash, stream.Published[1].PayloadHash);
            var gate = (IProcessingStreamDispatchGate)new ProcessingStreamStore(config);
            var rejected = await gate.ExecuteAsync("Published", WorkerInput(new { eventKey = stream.Published[0].EventKey, leaseToken = Guid.NewGuid(), streamName = stream.StreamName, streamId = "1-0" }), default);
            Assert.Equal("LEASE_STALE", rejected.GetProperty("code").GetString());
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM processing_stream_deliveries"));
        });
    }
}

internal sealed class MemoryProcessingStream : IProcessingStream
{
    public string StreamName => "fet3d:tests:processing";
    public List<ProcessingEnvelope> Published { get; } = [];
    public bool LoseResponse { get; set; }
    public Task<string> PublishAsync(ProcessingEnvelope envelope, CancellationToken ct)
    {
        lock (Published)
        {
            Published.Add(envelope);
            if (LoseResponse) throw new IOException("Simulated lost Redis response");
            return Task.FromResult($"{Published.Count}-0");
        }
    }
    public Task<IReadOnlyList<ProcessingStreamMessage>> ReadAsync(string consumer, CancellationToken ct) => Task.FromResult<IReadOnlyList<ProcessingStreamMessage>>([]);
    public Task<IReadOnlyList<ProcessingStreamMessage>> ReclaimAsync(string consumer, CancellationToken ct) => Task.FromResult<IReadOnlyList<ProcessingStreamMessage>>([]);
    public Task AcknowledgeAsync(string id, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> ConnectedAsync(CancellationToken ct) => Task.FromResult(true);
}
