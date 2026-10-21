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
    [PostgresFact]
    public async Task Redis_retry_api_takes_building_advisory_before_scope_row_locks()
    {
        var revision = await SeedVerifiedIfcRevision();
        await using var requestDb = BuildingContext(testConnection);
        var job = (await new ProcessingRuntimeStore(requestDb, new ConfigurationBuilder().Build())
            .RequestAsync(adminId, revision, "lock-order-job", default)).Value;
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        await using var holder = new NpgsqlConnection(testConnection);
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", holder, transaction))
        {
            command.Parameters.AddWithValue("key", "fire3d:building:" + building);
            await command.ExecuteNonQueryAsync();
        }
        var applicationName = "retry-order-" + Guid.NewGuid().ToString("N");
        await using var retryDb = BuildingContext(new NpgsqlConnectionStringBuilder(testConnection)
            { ApplicationName = applicationName, Pooling = false }.ConnectionString);
        var retry = new IfcWriteStore(retryDb).RetryJobAsync(adminId, job, Guid.NewGuid(), "Retry", default);
        try
        {
            var waiting = false;
            for (var i = 0; i < 150; i++)
            {
                waiting = (bool)(await ScalarAsync($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='{applicationName}' AND wait_event='advisory')"))!;
                if (waiting) break;
                await Task.Delay(20);
            }
            Assert.True(waiting, "Retry must wait for the building advisory lock.");
            // Models release_access_gate: its advisory lock must let it update the building without waiting on retry's scope row lock.
            await using var update = new NpgsqlCommand("SET LOCAL lock_timeout='750ms'; UPDATE buildings SET is_active=is_active WHERE id=@building", holder, transaction);
            update.Parameters.AddWithValue("building", building);
            await update.ExecuteNonQueryAsync();
        }
        finally
        {
            await transaction.RollbackAsync();
            await retry;
        }
        Assert.Equal("JOB_NOT_CLAIMABLE", (await retry).Error?.Code);
    }

    [PostgresFact]
    public async Task Redis_recovery_reaches_work_after_twenty_blocked_deliveries()
    {
        await WithRedisRuntime(async config =>
        {
            using var scopes = RedisScope(config);
            var stream = new MemoryProcessingStream();
            var publisher = new RedisProcessingPublisher(scopes.GetRequiredService<IServiceScopeFactory>(), stream,
                Options.Create(new ProcessingWorkerOptions()), NullLogger<RedisProcessingPublisher>.Instance);
            for (var i = 0; i < 21; i++)
            {
                var revision = await SeedVerifiedIfcRevision();
                await using var db = BuildingContext(config.GetConnectionString("DefaultConnection")!);
                Assert.True((await new ProcessingRuntimeStore(db, config).RequestAsync(adminId, revision, $"recovery-{i}", default)).IsSuccess);
                Assert.True(await publisher.RunOnceAsync(default));
            }
            // The first twenty rows stay Published but cannot be retried automatically.
            await ExecuteAsync("""
                UPDATE integration_outbox_events SET published_at=now()-interval '1 hour';
                INSERT INTO processing_stream_state(event_key,blocked_reason)
                SELECT idempotency_key,'HANDOFF_RETRY_EXHAUSTED' FROM integration_outbox_events
                ORDER BY available_at,idempotency_key LIMIT 20;
                """);
            var recovery = (IProcessingStreamRecoveryGate)new ProcessingStreamStore(config);
            var result = await recovery.ExecuteAsync("ReplayMissing", WorkerInput(new
                { streamName = stream.StreamName, handoffTimeoutSeconds = 300 }), default);
            Assert.Equal(1, result.GetProperty("replayed").GetInt32());
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM integration_outbox_events WHERE status='Pending'"));
            Assert.Equal(20L, await ScalarAsync("SELECT count(*) FROM processing_stream_state WHERE blocked_reason IS NOT NULL"));
            // Deferred rows must also be filtered before LIMIT, not repeatedly occupy the batch.
            Assert.True(await publisher.RunOnceAsync(default));
            await ExecuteAsync("""
                UPDATE integration_outbox_events SET published_at=now()-interval '1 hour';
                UPDATE processing_stream_state SET next_delivery_at=CASE WHEN blocked_reason IS NOT NULL THEN now()+interval '1 hour' ELSE now()-interval '1 minute' END,
                    blocked_reason=NULL;
                """);
            Assert.Equal(1, (await recovery.ExecuteAsync("ReplayMissing", WorkerInput(new
                { streamName = stream.StreamName, handoffTimeoutSeconds = 300 }), default)).GetProperty("replayed").GetInt32());
        });
    }

    [PostgresFact]
    public async Task Redis_exhausted_publication_can_be_retried_once_without_rewriting_the_event()
        => await AssertExhaustedDeliveryRetry(handoff: false);

    [PostgresFact]
    public async Task Redis_exhausted_handoff_can_be_retried_once_without_rewriting_the_event()
        => await AssertExhaustedDeliveryRetry(handoff: true);

    private async Task AssertExhaustedDeliveryRetry(bool handoff)
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var db = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var job = (await new ProcessingRuntimeStore(db, config).RequestAsync(adminId, revision, "exhausted", default)).Value;
            var dispatch = (IProcessingStreamDispatchGate)new ProcessingStreamStore(config);
            var consumer = (IProcessingStreamConsumerGate)new ProcessingStreamStore(config);
            var delivery = await dispatch.ExecuteAsync("Claim", WorkerInput(new { streamName = "fet3d:tests:processing" }), default);
            var envelope = delivery.GetProperty("envelope");
            var eventKey = envelope.GetProperty("eventKey").GetString()!;
            if (handoff)
            {
                await dispatch.ExecuteAsync("Published", WorkerInput(new { eventKey, leaseToken = delivery.GetProperty("leaseToken").GetGuid(), streamName = "fet3d:tests:processing", streamId = "1-0" }), default);
                for (var i = 0; i < 10; i++) await consumer.ExecuteAsync("Failure", WorkerInput(new { envelope }), default);
            }
            else
            {
                await ExecuteAsync("UPDATE integration_outbox_events SET attempts=10");
                await dispatch.ExecuteAsync("Fail", WorkerInput(new { eventKey, leaseToken = delivery.GetProperty("leaseToken").GetGuid() }), default);
            }
            Assert.Equal("Queued", await ScalarAsync($"SELECT status::text FROM processing_jobs WHERE id='{job}'"));
            var apiUser = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection")!).Username;
            await ExecuteAsync($"GRANT EXECUTE ON FUNCTION requeue_processing_job(uuid,text,text) TO {apiUser}");
            async Task<string> Retry(string key, string reason = "Retry delivery")
            {
                await using var connection = new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT requeue_processing_job(@job,@key,@reason)", connection);
                command.Parameters.AddWithValue("job", job);
                command.Parameters.AddWithValue("key", key);
                command.Parameters.AddWithValue("reason", reason);
                return (string)(await command.ExecuteScalarAsync())!;
            }
            var keys = handoff ? new[] { "delivery-retry-a", "delivery-retry-b" } : new[] { "delivery-retry", "delivery-retry" };
            var retried = await Task.WhenAll(Retry(keys[0]), Retry(keys[1]));
            Assert.Single(retried, x => x == "Requeued");
            Assert.Single(retried, x => x == (handoff ? "NotClaimable" : "AlreadyRequeued"));
            var retryKey = keys[Array.IndexOf(retried, "Requeued")];
            Assert.Equal("NotClaimable", await Retry("unnecessary-retry"));
            await Assert.ThrowsAsync<PostgresException>(() => Retry(retryKey, "Changed reason"));
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM integration_outbox_events"));
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM processing_stream_retry_receipts"));
            Assert.Equal(false, await ScalarAsync($"SELECT has_table_privilege('{apiUser}','processing_stream_retry_receipts','INSERT')"));
            Assert.Equal("OK", (await consumer.ExecuteAsync("Inspect", WorkerInput(new { envelope }), default)).GetProperty("code").GetString());
            var next = await dispatch.ExecuteAsync("Claim", WorkerInput(new { streamName = "fet3d:tests:processing" }), default);
            Assert.Equal(eventKey, next.GetProperty("envelope").GetProperty("eventKey").GetString());
            Assert.Equal(envelope.GetProperty("payloadHash").GetString(), next.GetProperty("envelope").GetProperty("payloadHash").GetString());
            Assert.Equal("AlreadyRequeued", await Retry(retryKey));
            Assert.Equal(1, await ScalarAsync("SELECT attempts FROM integration_outbox_events"));
            var runtime = new ProcessingRuntimeStore(db, config);
            var claimed = await runtime.ExecuteAsync("Claim", job, WorkerInput(new { eventKey,
                payloadHash = envelope.GetProperty("payloadHash").GetString(), inputHash = IfcHash, toolchainVersion = "fake-v1" }), default);
            Assert.True(claimed.IsSuccess, claimed.Error?.Code);
            Assert.Equal("NotClaimable", await Retry("running-retry"));
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM processing_job_attempts"));
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM processing_delivery_receipts"));
            // Even a queued job with an expired attempt must not reset a delivery already handed off.
            await ExecuteAsync($"UPDATE processing_job_attempts SET status='Expired'; UPDATE processing_jobs SET status='Queued' WHERE id='{job}'; UPDATE processing_stream_state SET blocked_reason='HANDOFF_RETRY_EXHAUSTED'");
            Assert.Equal("NotClaimable", await Retry("consumed-retry"));
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM processing_delivery_receipts"));
        });
    }
}
