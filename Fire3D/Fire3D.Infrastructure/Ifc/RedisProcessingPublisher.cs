using System.Text.Json;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Ifc;

public sealed class RedisProcessingPublisher(IServiceScopeFactory scopes, IProcessingStream stream,
    IOptions<ProcessingWorkerOptions> options, ILogger<RedisProcessingPublisher> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatcherEnabled || options.Value.Transport != "RedisStreams") return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollSeconds));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Redis publisher unavailable: {ErrorType}", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IProcessingStreamDispatchGate>();
        var claim = await gate.ExecuteAsync("Claim", JsonSerializer.SerializeToElement(new { streamName = stream.StreamName }), ct);
        if (claim.GetProperty("code").GetString() != "OK") return false;
        var envelope = claim.GetProperty("envelope").Deserialize<ProcessingEnvelope>(Json)!;
        var leaseToken = claim.GetProperty("leaseToken").GetGuid();
        try
        {
            var id = await stream.PublishAsync(envelope, ct);
            var result = await gate.ExecuteAsync("Published", JsonSerializer.SerializeToElement(new
                { eventKey = envelope.EventKey, leaseToken, streamName = stream.StreamName, streamId = id }), ct);
            log.LogInformation("Redis publication: EventKey={EventKey}, JobId={JobId}, StreamId={StreamId}, Result={Result}", envelope.EventKey, envelope.AggregateId, id, result.GetProperty("code").GetString());
            return result.GetProperty("code").GetString() == "OK";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("Redis publication failed: EventKey={EventKey}, ErrorType={ErrorType}", envelope.EventKey, ex.GetType().Name);
            await gate.ExecuteAsync("Fail", JsonSerializer.SerializeToElement(new { eventKey = envelope.EventKey, leaseToken }), ct);
            return false;
        }
    }
}
