using System.Text.Json;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Fire3D.Infrastructure.Ifc;

public sealed class RedisProcessingRecovery(IServiceScopeFactory scopes, IProcessingStream stream,
    IOptions<ProcessingWorkerOptions> worker, IOptions<RedisProcessingOptions> redis, ILogger<RedisProcessingRecovery> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (worker.Value.Transport != "RedisStreams" || (!worker.Value.DispatcherEnabled && !worker.Value.ConsumerEnabled)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(redis.Value.RecoverySeconds));
        do
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Processing Redis recovery unavailable: {ErrorType}", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IProcessingStreamRecoveryGate>();
        await gate.ExecuteAsync("RecoverAttempt", JsonSerializer.SerializeToElement(new { }), ct);
        var replay = await gate.ExecuteAsync("ReplayMissing", JsonSerializer.SerializeToElement(new { streamName = stream.StreamName, handoffTimeoutSeconds = redis.Value.HandoffTimeoutSeconds }, Json), ct);
        var trimmed = await stream.TrimAsync(async (message, cancellation) =>
        {
            ProcessingEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize<ProcessingEnvelope>(message.Envelope, Json); }
            catch (JsonException) { return false; }
            if (!RedisProcessingConsumer.Valid(envelope)) return false;
            var result = await gate.ExecuteAsync("CanTrim", JsonSerializer.SerializeToElement(new { envelope, streamName = stream.StreamName, streamId = message.Id }, Json), cancellation);
            return result.GetProperty("code").GetString() == "SAFE";
        }, ct);
        var backlog = await stream.BacklogAsync(ct);
        log.LogInformation("Processing Redis recovery: Replayed={Replayed}, Trimmed={Trimmed}, StreamLength={StreamLength}, Pending={Pending}", replay.GetProperty("replayed").GetInt32(), trimmed, backlog.Length, backlog.Pending);
    }
}
