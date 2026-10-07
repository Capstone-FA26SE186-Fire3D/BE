using System.Text.Json;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace Fire3D.Infrastructure.Ifc;

public sealed class RedisProcessingConsumer(IServiceScopeFactory scopes, IProcessingStream stream, IHttpClientFactory clients,
    IOptions<ProcessingWorkerOptions> options, ILogger<RedisProcessingConsumer> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string consumer = Environment.MachineName + "-" + Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ConsumerEnabled || options.Value.Transport != "RedisStreams") return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollSeconds));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Redis consumer unavailable: {ErrorType}", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var message in await stream.ReadAsync(consumer, ct)) await HandleAsync(message, ct);
        foreach (var message in await stream.ReclaimAsync(consumer, ct)) await HandleAsync(message, ct);
    }

    public async Task<bool> HandleAsync(ProcessingStreamMessage message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IProcessingStreamConsumerGate>();
        ProcessingEnvelope? envelope = null;
        try { envelope = JsonSerializer.Deserialize<ProcessingEnvelope>(message.Envelope, Json); }
        catch (JsonException) { }
        if (!Valid(envelope))
        {
            await gate.ExecuteAsync("Reject", Input(new { streamName = stream.StreamName, streamId = message.Id, code = "INVALID_ENVELOPE" }), ct);
            return false;
        }
        var input = Input(new { envelope, streamName = stream.StreamName, streamId = message.Id });
        var inspection = await gate.ExecuteAsync("Inspect", input, ct);
        var code = inspection.GetProperty("code").GetString();
        if (code == "CONSUMED") { await stream.AcknowledgeAsync(message.Id, ct); return true; }
        if (code != "OK") return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            // Forward only the authoritative envelope; no publisher/worker lease is supplied.
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.WorkerUrl)
            { Content = JsonContent.Create(envelope, options: Json) };
            request.Headers.Add("X-Worker-Key", options.Value.MachineKey);
            using var response = await clients.CreateClient("processing-worker").SendAsync(request, timeout.Token);
            log.LogInformation("Redis handoff: EventKey={EventKey}, StreamId={StreamId}, ProviderStatus={ProviderStatus}", envelope!.EventKey, message.Id, (int)response.StatusCode);
            if (response.IsSuccessStatusCode)
            {
                var reply = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                if (reply.TryGetProperty("eventKey", out var key) && key.GetString() == envelope.EventKey &&
                    reply.TryGetProperty("payloadHash", out var hash) && hash.GetString() == envelope.PayloadHash &&
                    reply.TryGetProperty("receiptId", out var receipt) && receipt.TryGetGuid(out var receiptId))
                {
                    var confirmed = await gate.ExecuteAsync("Confirm", Input(new { envelope, receiptId }), ct);
                    if (confirmed.GetProperty("code").GetString() == "CONSUMED")
                    { await stream.AcknowledgeAsync(message.Id, ct); return true; }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("Redis handoff interrupted: EventKey={EventKey}, StreamId={StreamId}, ErrorType={ErrorType}", envelope!.EventKey, message.Id, ex.GetType().Name);
        }
        // A lost HTTP response or Redis ACK must not cause a second provider effect.
        if ((await gate.ExecuteAsync("Inspect", input, ct)).GetProperty("code").GetString() == "CONSUMED")
        { await stream.AcknowledgeAsync(message.Id, ct); return true; }
        await gate.ExecuteAsync("Failure", input, ct);
        return false;
    }

    public static bool Valid(ProcessingEnvelope? envelope) => envelope is not null &&
        !string.IsNullOrWhiteSpace(envelope.EventKey) && envelope.EventKey.Length <= 255 && !envelope.EventKey.Any(char.IsControl) &&
        envelope.EventType is "ProcessingJobRequested" or "ProcessingJobRequeue" && envelope.SchemaVersion == "1" &&
        envelope.AggregateType == "ProcessingJob" && envelope.AggregateId != Guid.Empty && envelope.OrganizationId != Guid.Empty &&
        envelope.PayloadHash is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit) && envelope.Payload.ValueKind == JsonValueKind.Object &&
        envelope.Payload.TryGetProperty("job_id", out var job) && job.ValueKind == JsonValueKind.String && job.TryGetGuid(out var id) && id == envelope.AggregateId;

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value, Json);
}
