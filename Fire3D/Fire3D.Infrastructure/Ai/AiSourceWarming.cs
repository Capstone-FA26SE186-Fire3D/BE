using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fire3D.Application.Ai;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Ai;

/// <summary>
/// POST /v2/sources/warm of the AI service (BE#187): queues knowledge sources for embedding before anyone asks, so a question does not
/// wait for a large PDF to be embedded. The answer is 202 {"sources":[{document_id,state,code?}]}; asking again shows the progress.
/// </summary>
public sealed class FastApiSourceWarmer(IHttpClientFactory clients, IOptions<OrganizationAiOptions> options, ILogger<FastApiSourceWarmer> log) : IAiSourceWarmer
{
    public const int MaxPins = 100;

    public async Task<IReadOnlyList<AiSourceIndexState>?> WarmAsync(IReadOnlyList<AiSourcePin> pins, CancellationToken ct)
    {
        if (pins.Count == 0) return [];
        if (!options.Value.Configured) return null;
        var states = new List<AiSourceIndexState>();
        foreach (var chunk in pins.Chunk(MaxPins))
        {
            var part = await WarmChunkAsync(chunk, ct);
            if (part is null) return null;
            states.AddRange(part);
        }
        return states;
    }

    private async Task<List<AiSourceIndexState>?> WarmChunkAsync(AiSourcePin[] chunk, CancellationToken ct)
    {
        var o = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(o.BaseUrl!.TrimEnd('/') + "/"), "v2/sources/warm"))
        {
            Content = JsonContent.Create(new { sources = chunk.Select(Body) })
        };
        if (!string.IsNullOrEmpty(o.ApiKey)) request.Headers.Authorization = new("Bearer", o.ApiKey);
        try
        {
            using var response = await clients.CreateClient(FastApiAiClient.ClientName).SendAsync(request, timeout.Token);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                log.LogWarning("The AI service answered {Status} to a source warm request.", (int)response.StatusCode);
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
            var known = chunk.Select(x => x.SourceId).ToHashSet();
            var states = new List<AiSourceIndexState>();
            foreach (var item in body?["sources"]?.AsArray() ?? [])
            {
                if (item is not JsonObject entry || !Guid.TryParse(Text(entry, "document_id"), out var id) || !known.Contains(id)) continue;
                states.Add(new(id, Text(entry, "state") ?? "unknown", AiRefusal.Plain(Text(entry, "code"), 80)));
            }
            return states;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            log.LogWarning("A source warm request to the AI service failed: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    // A knowledge source version is the source row itself, as in a dispatch pin.
    private static object Body(AiSourcePin pin) => new
    {
        kind = "KnowledgeSource",
        document_id = pin.SourceId,
        document_version = pin.SourceId,
        title = pin.Title,
        version_label = pin.VersionLabel,
        content_hash = pin.ContentHash,
        source_uri = pin.SourceUri,
        content_omitted = false
    };

    private static string? Text(JsonObject node, string name) => node[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>What the AI service last said about each source, and the queue of sources waiting to be warmed. One per API instance.</summary>
public sealed class AiSourceIndex : IAiSourceIndex
{
    private readonly ConcurrentDictionary<Guid, AiIndexInfo> known = new();
    private readonly Channel<AiSourcePin> queue = Channel.CreateUnbounded<AiSourcePin>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<AiSourcePin> Pending => queue.Reader;

    public void Record(IEnumerable<AiSourceIndexState> states)
    {
        foreach (var state in states) known[state.SourceId] = AiIndexStatus.From(state);
    }

    public AiIndexInfo Of(Guid sourceId) => known.TryGetValue(sourceId, out var info) ? info : new(AiIndexStatus.Unknown, null);

    public void Enqueue(AiSourcePin pin) => queue.Writer.TryWrite(pin);
}

/// <summary>
/// Warms approved sources in the background with a few retries (BE#187). A failed or unreachable AI service never fails the approval that
/// asked for it; the Admin warm-all action covers what a restart or an outage lost.
/// </summary>
public sealed class KnowledgeSourceWarmWorker(AiSourceIndex index, IAiSourceWarmer warmer, IOptions<OrganizationAiOptions> options,
    ILogger<KnowledgeSourceWarmWorker> log, Func<TimeSpan, CancellationToken, Task>? delay = null) : BackgroundService
{
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)];
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Configured) return;
        try
        {
            await foreach (var first in index.Pending.ReadAllAsync(stoppingToken))
            {
                var batch = new List<AiSourcePin> { first };
                while (batch.Count < FastApiSourceWarmer.MaxPins && index.Pending.TryRead(out var more)) batch.Add(more);
                await ProcessAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task ProcessAsync(IReadOnlyList<AiSourcePin> batch, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            IReadOnlyList<AiSourceIndexState>? states = null;
            try { states = await warmer.WarmAsync(batch, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log.LogWarning("Warming knowledge sources failed: {ErrorType}", ex.GetType().Name); }
            if (states is not null) { index.Record(states); return; }
            if (attempt >= RetryDelays.Length)
            {
                log.LogWarning("The AI service did not take {Count} knowledge source(s) to warm after {Attempts} attempts; use the Admin warm-all action later.", batch.Count, attempt + 1);
                return;
            }
            await wait(RetryDelays[attempt], ct);
        }
    }
}
