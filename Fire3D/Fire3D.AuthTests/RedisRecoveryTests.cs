using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;
namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Redis_cancelled_last_publisher_lease_is_exhausted_and_terminal_jobs_are_not_published()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var context = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var job = (await new ProcessingRuntimeStore(context, config).RequestAsync(adminId, revision, "redis-last-lease", default)).Value;
            var dispatch = (IProcessingStreamDispatchGate)new ProcessingStreamStore(config);
            var recovery = (IProcessingStreamRecoveryGate)new ProcessingStreamStore(config);
            var claimed = await dispatch.ExecuteAsync("Claim", WorkerInput(new { streamName = "fet3d:test:processing" }), default);
            Assert.Equal("OK", claimed.GetProperty("code").GetString());
            await ExecuteAsync("UPDATE integration_outbox_events SET attempts=10,lease_until=now()-interval '1 minute'");
            await recovery.ExecuteAsync("ReplayMissing", WorkerInput(new { streamName = "fet3d:test:processing", handoffTimeoutSeconds = 300 }), default);
            Assert.Equal("Failed", await ScalarAsync("SELECT status::text FROM integration_outbox_events"));
            Assert.Equal("EMPTY", (await dispatch.ExecuteAsync("Claim", WorkerInput(new { streamName = "fet3d:test:processing" }), default)).GetProperty("code").GetString());
            await ExecuteAsync($"UPDATE processing_jobs SET status='Cancelled' WHERE id='{job}'; UPDATE integration_outbox_events SET status='Pending',attempts=0,available_at=now()");
            Assert.Equal("EMPTY", (await dispatch.ExecuteAsync("Claim", WorkerInput(new { streamName = "fet3d:test:processing" }), default)).GetProperty("code").GetString());
            var envelope = claimed.GetProperty("envelope");
            Assert.Equal("JOB_TERMINAL", (await ((IProcessingStreamConsumerGate)new ProcessingStreamStore(config)).ExecuteAsync("Inspect", WorkerInput(new { envelope, streamName = "fet3d:test:processing", streamId = "1-0" }), default)).GetProperty("code").GetString());
            var apiUser = new Npgsql.NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection")!).Username;
            Assert.Equal(false, await ScalarAsync($"SELECT has_function_privilege('{apiUser}','processing_stream_consumer_gate_before_recovery(text,jsonb)','EXECUTE')"));
            Assert.Equal(false, await ScalarAsync($"SELECT has_function_privilege('{apiUser}','processing_worker_gate_before_streams(text,uuid,jsonb)','EXECUTE')"));
        });
    }

    [RedisPostgresFact]
    public async Task Redis_missing_stream_replays_published_event_and_group_loss_reloads_from_zero()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var context = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var job = (await new ProcessingRuntimeStore(context, config).RequestAsync(adminId, revision, "redis-recovery", default)).Value;
            var options = RedisStreamIntegrationTests.TestOptions();
            await using var stream = new RedisProcessingStream(Options.Create(options), RedisStreamIntegrationTests.TestEnvironment("test-" + Guid.NewGuid().ToString("N")));
            await using var connection = await RedisStreamIntegrationTests.ConnectAsync(options);
            using var scopes = RedisScope(config);
            try
            {
                var publisher = new RedisProcessingPublisher(scopes.GetRequiredService<IServiceScopeFactory>(), stream, Options.Create(new ProcessingWorkerOptions()), NullLogger<RedisProcessingPublisher>.Instance);
                Assert.True(await publisher.RunOnceAsync(default));
                var first = Assert.Single(await stream.ReadAsync("dead", default));
                await connection.GetDatabase().KeyDeleteAsync(stream.StreamName);
                await ExecuteAsync("UPDATE integration_outbox_events SET published_at=now()-interval '10 minutes'");
                var recovery = (IProcessingStreamRecoveryGate)new ProcessingStreamStore(config);
                var input = WorkerInput(new { streamName = stream.StreamName, handoffTimeoutSeconds = 300 });
                var concurrent = await Task.WhenAll(recovery.ExecuteAsync("ReplayMissing", input, default), recovery.ExecuteAsync("ReplayMissing", input, default));
                Assert.Equal(1, concurrent.Sum(x => x.GetProperty("replayed").GetInt32()));
                Assert.True(await publisher.RunOnceAsync(default));
                var second = Assert.Single(await stream.ReadAsync("alive", default));
                Assert.Equal(first.Envelope, second.Envelope);
                Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM processing_stream_deliveries"));
                Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM processing_jobs WHERE id='{job}'"));
                // Removing only the group must restore reading from the beginning, not '$'.
                Assert.True(await connection.GetDatabase().StreamDeleteConsumerGroupAsync(stream.StreamName, options.Group));
                Assert.Equal(second.Id, Assert.Single(await stream.ReadAsync("replacement", default)).Id);
                await ExecuteAsync($"UPDATE processing_jobs SET status='Failed' WHERE id='{job}';UPDATE integration_outbox_events SET published_at=now()-interval '10 minutes';UPDATE processing_stream_state SET next_delivery_at=now()-interval '1 minute'");
                Assert.Equal(0, (await recovery.ExecuteAsync("ReplayMissing", input, default)).GetProperty("replayed").GetInt32());
                Assert.Equal("PROCESSING_JOB_TERMINAL", await ScalarAsync("SELECT blocked_reason FROM processing_stream_state"));
            }
            finally { await connection.GetDatabase().KeyDeleteAsync(stream.StreamName); }
        });
    }

    [PostgresFact]
    public async Task Redis_failure_backoff_exhaustion_and_consumed_event_are_not_replayed()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var db = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var runtime = new ProcessingRuntimeStore(db, config);
            var job = (await runtime.RequestAsync(adminId, revision, "redis-exhaust", default)).Value;
            var dispatch = (IProcessingStreamDispatchGate)new ProcessingStreamStore(config);
            var consumer = (IProcessingStreamConsumerGate)new ProcessingStreamStore(config);
            var recovery = (IProcessingStreamRecoveryGate)new ProcessingStreamStore(config);
            var delivery = await dispatch.ExecuteAsync("Claim", WorkerInput(new { streamName = "fet3d:test:processing" }), default);
            var envelope = delivery.GetProperty("envelope");
            var inspectInput = WorkerInput(new { envelope, streamName = "fet3d:test:processing", streamId = "1-0" });
            for (var i = 0; i < 10; i++) await consumer.ExecuteAsync("Failure", inspectInput, default);
            Assert.Equal("BLOCKED", (await consumer.ExecuteAsync("Inspect", inspectInput, default)).GetProperty("code").GetString());
            Assert.Equal(10, await ScalarAsync("SELECT delivery_failures FROM processing_stream_state"));
            Assert.True((bool)(await ScalarAsync("SELECT next_delivery_at>now()+interval '20 minutes' FROM processing_stream_state"))!);
            var claimed = await runtime.ExecuteAsync("Claim", job, WorkerInput(new { eventKey = envelope.GetProperty("eventKey").GetString(), payloadHash = envelope.GetProperty("payloadHash").GetString(), inputHash = IfcHash, toolchainVersion = "fake-v1" }), default);
            Assert.True(claimed.IsSuccess);
            await dispatch.ExecuteAsync("Published", WorkerInput(new { eventKey = envelope.GetProperty("eventKey").GetString(), leaseToken = delivery.GetProperty("leaseToken").GetGuid(), streamName = "fet3d:test:processing", streamId = "1-0" }), default);
            await ExecuteAsync("UPDATE integration_outbox_events SET published_at=now()-interval '10 minutes'");
            Assert.Equal(0, (await recovery.ExecuteAsync("ReplayMissing", WorkerInput(new { streamName = "fet3d:test:processing", handoffTimeoutSeconds = 300 }), default)).GetProperty("replayed").GetInt32());
            Assert.Equal("SAFE", (await recovery.ExecuteAsync("CanTrim", inspectInput, default)).GetProperty("code").GetString());
            Assert.Equal("PROTECTED", (await recovery.ExecuteAsync("CanTrim", WorkerInput(new { envelope = new { eventKey = "unknown" } }), default)).GetProperty("code").GetString());
        });
    }
}

public sealed class RedisRetentionTests
{
    [RedisFact]
    public async Task Redis_two_consumers_reclaim_dead_owner_and_retention_preserves_pending_unread_unconfirmed()
    {
        var options = RedisStreamIntegrationTests.TestOptions();
        await using var stream = new RedisProcessingStream(Options.Create(options), RedisStreamIntegrationTests.TestEnvironment("test-" + Guid.NewGuid().ToString("N")));
        await using var connection = await RedisStreamIntegrationTests.ConnectAsync(options);
        var db = connection.GetDatabase();
        try
        {
            var old = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds();
            var envelope = JsonSerializer.Serialize(RedisStreamIntegrationTests.Envelope(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            for (var i = 0; i < 4; i++) await db.StreamAddAsync(stream.StreamName, [new NameValueEntry("envelope", envelope)], $"{old}-{i}");
            await db.StreamCreateConsumerGroupAsync(stream.StreamName, options.Group, "0-0");
            var read = await db.StreamReadGroupAsync(stream.StreamName, options.Group, "dead", ">", 3);
            Assert.Equal(3, read.Length);
            await db.StreamAcknowledgeAsync(stream.StreamName, options.Group, [read[0].Id, read[2].Id]);
            // One ACKed entry is unconfirmed in PostgreSQL; pending and unread are protected independently.
            Assert.Equal(1, await stream.TrimAsync((m, _) => Task.FromResult(m.Id != read[2].Id.ToString()), default));
            Assert.Equal(3, await db.StreamLengthAsync(stream.StreamName));
            Assert.Equal(1, (await db.StreamPendingAsync(stream.StreamName, options.Group)).PendingMessageCount);
            await db.ExecuteAsync("XCLAIM", stream.StreamName, options.Group, "dead", 0, read[1].Id, "IDLE", 65000);
            Assert.Equal(read[1].Id.ToString(), Assert.Single(await stream.ReclaimAsync("replacement", default)).Id);
            var parallel = await Task.WhenAll(stream.ReadAsync("one", default), stream.ReadAsync("two", default));
            Assert.Single(parallel.SelectMany(x => x));
            await db.StreamCreateConsumerGroupAsync(stream.StreamName, "unexpected", "0-0");
            Assert.Equal(0, await stream.TrimAsync((_, _) => Task.FromResult(true), default));
        }
        finally { await db.KeyDeleteAsync(stream.StreamName); }
    }
}
