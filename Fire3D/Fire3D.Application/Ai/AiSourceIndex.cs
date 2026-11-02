namespace Fire3D.Application.Ai;

/// <summary>A knowledge source as the AI service needs it to prepare (embed) it before anyone asks: the same fields a dispatch pin carries.</summary>
public sealed record AiSourcePin(Guid SourceId, string Title, string? VersionLabel, string ContentHash, string? SourceUri);

/// <summary>State the AI service reported for a source: ready, queued (being embedded) or failed (Code says why, e.g. AI_SOURCE_UNAVAILABLE).</summary>
public sealed record AiSourceIndexState(Guid SourceId, string State, string? Code);

/// <summary>What Admin sees next to a source (BE#187): Ready, Preparing, Failed (with the code) or Unknown (never reported by the AI service).</summary>
public sealed record AiIndexInfo(string Status, string? Error);

public interface IAiSourceWarmer
{
    /// <summary>POST /v2/sources/warm: queues the sources for embedding and returns their state. Null when the AI service is off or does not answer.</summary>
    Task<IReadOnlyList<AiSourceIndexState>?> WarmAsync(IReadOnlyList<AiSourcePin> pins, CancellationToken ct);
}

public interface IAiSourceIndex
{
    /// <summary>Remembers what the AI service last reported (per API instance; not stored in the database).</summary>
    void Record(IEnumerable<AiSourceIndexState> states);
    AiIndexInfo Of(Guid sourceId);
    /// <summary>Asks the background worker to warm a source, with retries. Never throws and never blocks the caller.</summary>
    void Enqueue(AiSourcePin pin);
}

public static class AiIndexStatus
{
    public const string Ready = "Ready";
    public const string Preparing = "Preparing";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";

    public static AiIndexInfo From(AiSourceIndexState state) => state.State switch
    {
        "ready" => new(Ready, null),
        "queued" => new(Preparing, null),
        "failed" => new(Failed, state.Code),
        _ => new(Unknown, null)
    };
}

/// <summary>One source in the answer to the Admin warm-all action.</summary>
public sealed record KnowledgeSourceWarmItem(Guid Id, string Title, string Status, string? Error);

/// <summary>Status is Ready, Preparing, Failed or Unknown (the AI service did not report that source).</summary>
public sealed record KnowledgeSourceWarmResult(int Requested, int Ready, int Preparing, int Failed, IReadOnlyList<KnowledgeSourceWarmItem> Sources);
