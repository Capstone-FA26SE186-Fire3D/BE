using System.Text.Json;

namespace Fire3D.Application.Ifc;

public sealed class RedisProcessingOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 10000;
    public bool Ssl { get; set; } = true;
    public string Password { get; set; } = "";
    public string Group { get; set; } = "fet3d-processing-bridge-v1";
    public int PendingIdleSeconds { get; set; } = 60;
    public int RecoverySeconds { get; set; } = 30;
    public int HandoffTimeoutSeconds { get; set; } = 300;
    public int RetentionDays { get; set; } = 7;
}

// The PostgreSQL envelope/hash is authoritative. Stream IDs identify deliveries, not events.
public sealed record ProcessingEnvelope(string EventKey, string EventType, string SchemaVersion,
    string AggregateType, Guid AggregateId, Guid OrganizationId, JsonElement Payload, string PayloadHash);
public sealed record ProcessingStreamMessage(string Id, string Envelope);

public interface IProcessingStream
{
    string StreamName { get; }
    Task<string> PublishAsync(ProcessingEnvelope envelope, CancellationToken ct);
    Task<IReadOnlyList<ProcessingStreamMessage>> ReadAsync(string consumer, CancellationToken ct);
    Task<IReadOnlyList<ProcessingStreamMessage>> ReclaimAsync(string consumer, CancellationToken ct);
    Task AcknowledgeAsync(string id, CancellationToken ct);
    Task<bool> ConnectedAsync(CancellationToken ct);
}
