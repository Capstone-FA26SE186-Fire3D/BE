using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [RedisPostgresFact]
    public async Task Redis_consumer_lost_HTTP_response_and_ACK_replay_one_durable_handoff()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var db = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var runtime = new ProcessingRuntimeStore(db, config);
            var job = (await runtime.RequestAsync(adminId, revision, "redis-consumer", default)).Value;
            var redisOptions = RedisStreamIntegrationTests.TestOptions();
            await using var stream = new RedisProcessingStream(Options.Create(redisOptions), RedisStreamIntegrationTests.TestEnvironment("test-" + Guid.NewGuid().ToString("N")));
            await using var connection = await RedisStreamIntegrationTests.ConnectAsync(redisOptions);
            using var scopes = RedisScope(config);
            try
            {
                var publisher = new RedisProcessingPublisher(scopes.GetRequiredService<IServiceScopeFactory>(), stream, Options.Create(new ProcessingWorkerOptions()), NullLogger<RedisProcessingPublisher>.Instance);
                Assert.True(await publisher.RunOnceAsync(default));
                var message = Assert.Single(await stream.ReadAsync("reader", default));
                var httpCalls = 0;
                var handler = new FakeProcessingHttp(async request =>
                {
                    Assert.Equal(new string('k', 32), request.Headers.GetValues("X-Worker-Key").Single());
                    var envelope = await request.Content!.ReadFromJsonAsync<ProcessingEnvelope>();
                    var claim = await runtime.ExecuteAsync("Claim", job, WorkerInput(new { eventKey = envelope!.EventKey, payloadHash = envelope.PayloadHash, inputHash = IfcHash, toolchainVersion = "fake-v1" }), default);
                    Assert.True(claim.IsSuccess, claim.Error?.Code);
                    Interlocked.Increment(ref httpCalls);
                    throw new HttpRequestException("Simulated lost HTTP response after receipt commit");
                });
                var fault = new AckFaultStream(stream) { ThrowAck = true };
                var consumer = new RedisProcessingConsumer(scopes.GetRequiredService<IServiceScopeFactory>(), fault,
                    ResetProxy.For<IHttpClientFactory>((_, _) => new HttpClient(handler, false)),
                    Options.Create(new ProcessingWorkerOptions { WorkerUrl = "https://worker.example.test/deliver", MachineKey = new string('k', 32) }), NullLogger<RedisProcessingConsumer>.Instance);
                await Assert.ThrowsAsync<IOException>(() => consumer.HandleAsync(message, default));
                Assert.Equal(1L, (await connection.GetDatabase().StreamPendingAsync(stream.StreamName, redisOptions.Group)).PendingMessageCount);
                // Recovery must recognize committed handoff even after this attempt is expired.
                await ExecuteAsync($"UPDATE processing_job_attempts SET status='Expired',lease_until=now()-interval '1 second' WHERE processing_job_id='{job}'");
                fault.ThrowAck = false;
                Assert.True(await consumer.HandleAsync(message, default));
                Assert.Equal(1, httpCalls);
                Assert.Equal(0L, (await connection.GetDatabase().StreamPendingAsync(stream.StreamName, redisOptions.Group)).PendingMessageCount);
                Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM processing_job_attempts WHERE processing_job_id='{job}'"));
                Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM integration_event_consumptions WHERE consumer_name='processing-bridge-v1'"));
            }
            finally { await connection.GetDatabase().KeyDeleteAsync(stream.StreamName); }
        });
    }

    [RedisPostgresFact]
    public async Task Redis_consumer_rejects_scope_and_hash_mismatch_without_ACK_or_worker_call()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var db = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            await new ProcessingRuntimeStore(db, config).RequestAsync(adminId, revision, "redis-invalid", default);
            var dispatch = (IProcessingStreamDispatchGate)new ProcessingStreamStore(config);
            var claim = await dispatch.ExecuteAsync("Claim", WorkerInput(new { streamName = "fet3d:test:processing" }), default);
            var envelope = claim.GetProperty("envelope").Deserialize<ProcessingEnvelope>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            using var scopes = RedisScope(config);
            var stream = new MemoryProcessingStream();
            var consumer = new RedisProcessingConsumer(scopes.GetRequiredService<IServiceScopeFactory>(), stream,
                ResetProxy.For<IHttpClientFactory>((_, _) => throw new Exception("Invalid envelope reached HTTP worker")), Options.Create(new ProcessingWorkerOptions()), NullLogger<RedisProcessingConsumer>.Instance);
            Assert.False(await consumer.HandleAsync(new("1-0", JsonSerializer.Serialize(envelope with { OrganizationId = Guid.NewGuid() }, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default));
            Assert.False(await consumer.HandleAsync(new("2-0", JsonSerializer.Serialize(envelope with { PayloadHash = new string('b', 64) }, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default));
            Assert.False(await consumer.HandleAsync(new("3-0", "{invalid"), default));
            Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM processing_stream_rejections"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM processing_job_attempts"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM integration_event_consumptions"));
        });
    }

    [PostgresFact]
    public async Task Redis_handoff_receipt_failure_rolls_back_attempt_and_claim_audit()
    {
        var revision = await SeedVerifiedIfcRevision();
        await WithRedisRuntime(async config =>
        {
            await using var db = BuildingContext(config.GetConnectionString("DefaultConnection")!);
            var runtime = new ProcessingRuntimeStore(db, config);
            var job = (await runtime.RequestAsync(adminId, revision, "redis-rollback", default)).Value;
            var gate = (IProcessingStreamDispatchGate)new ProcessingStreamStore(config);
            var delivery = await gate.ExecuteAsync("Claim", WorkerInput(new { streamName = "fet3d:test:processing" }), default);
            var envelope = delivery.GetProperty("envelope");
            var input = WorkerInput(new { eventKey = envelope.GetProperty("eventKey").GetString(), payloadHash = envelope.GetProperty("payloadHash").GetString(), inputHash = IfcHash, toolchainVersion = "fake-v1" });
            var auditCount = await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{job}'");
            await ExecuteAsync("CREATE FUNCTION reject_test_consumption() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected receipt failure'; END $$; CREATE TRIGGER reject_test_consumption BEFORE INSERT ON integration_event_consumptions FOR EACH ROW EXECUTE FUNCTION reject_test_consumption()");
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => runtime.ExecuteAsync("Claim", job, input, default));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM processing_job_attempts WHERE processing_job_id='{job}'"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM processing_delivery_receipts"));
            Assert.Equal(auditCount, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{job}'"));
            await ExecuteAsync("DROP TRIGGER reject_test_consumption ON integration_event_consumptions; DROP FUNCTION reject_test_consumption()");
            Assert.True((await runtime.ExecuteAsync("Claim", job, input, default)).IsSuccess);
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM integration_event_consumptions"));
        });
    }
}

internal sealed class AckFaultStream(IProcessingStream inner) : IProcessingStream
{
    public bool ThrowAck { get; set; }
    public string StreamName => inner.StreamName;
    public Task<string> PublishAsync(ProcessingEnvelope envelope, CancellationToken ct) => inner.PublishAsync(envelope, ct);
    public Task<IReadOnlyList<ProcessingStreamMessage>> ReadAsync(string consumer, CancellationToken ct) => inner.ReadAsync(consumer, ct);
    public Task<IReadOnlyList<ProcessingStreamMessage>> ReclaimAsync(string consumer, CancellationToken ct) => inner.ReclaimAsync(consumer, ct);
    public Task<bool> ConnectedAsync(CancellationToken ct) => inner.ConnectedAsync(ct);
    public Task AcknowledgeAsync(string id, CancellationToken ct) => ThrowAck ? throw new IOException("Simulated Redis ACK failure") : inner.AcknowledgeAsync(id, ct);
}
