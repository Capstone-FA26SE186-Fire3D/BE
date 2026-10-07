using System.Text.Json;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Fire3D.Infrastructure.Ifc;

public sealed class RedisProcessingStream(IOptions<RedisProcessingOptions> options, IHostEnvironment environment)
    : IProcessingStream, IAsyncDisposable
{
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private ConnectionMultiplexer? connection;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string StreamName => $"fet3d:{environment.EnvironmentName.ToLowerInvariant()}:processing";

    private async Task<IDatabase> DatabaseAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) throw new InvalidOperationException("Redis processing transport is disabled.");
        if (connection is null)
        {
            await connectionLock.WaitAsync(ct);
            try
            {
                if (connection is null)
                {
                    var config = new ConfigurationOptions
                    {
                        Ssl = options.Value.Ssl, Password = options.Value.Password,
                        AbortOnConnectFail = false, ConnectTimeout = 5000, AsyncTimeout = 5000,
                        BacklogPolicy = BacklogPolicy.FailFast, ClientName = "fet3d-processing"
                    };
                    config.EndPoints.Add(options.Value.Host, options.Value.Port);
                    // Do not abandon a connecting multiplexer on caller cancellation: retain and dispose it.
                    connection = await ConnectionMultiplexer.ConnectAsync(config);
                }
            }
            finally { connectionLock.Release(); }
        }
        ct.ThrowIfCancellationRequested();
        return connection.GetDatabase();
    }

    public async Task<string> PublishAsync(ProcessingEnvelope envelope, CancellationToken ct)
    {
        var db = await DatabaseAsync(ct);
        return (await db.StreamAddAsync(StreamName, [new NameValueEntry("envelope", JsonSerializer.Serialize(envelope, Json))]).WaitAsync(ct)).ToString();
    }

    private async Task<IDatabase> GroupAsync(CancellationToken ct)
    {
        var db = await DatabaseAsync(ct);
        try { await db.StreamCreateConsumerGroupAsync(StreamName, options.Value.Group, "0-0", createStream: true).WaitAsync(ct); }
        catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal)) { }
        return db;
    }

    private static ProcessingStreamMessage[] Messages(StreamEntry[] entries) => entries.Select(x =>
        new ProcessingStreamMessage(x.Id.ToString(), x.Values.FirstOrDefault(v => v.Name == "envelope").Value.ToString())).ToArray();

    public async Task<IReadOnlyList<ProcessingStreamMessage>> ReadAsync(string consumer, CancellationToken ct)
    {
        var db = await GroupAsync(ct);
        return Messages(await db.StreamReadGroupAsync(StreamName, options.Value.Group, consumer, ">", count: 20).WaitAsync(ct));
    }

    public async Task<IReadOnlyList<ProcessingStreamMessage>> ReclaimAsync(string consumer, CancellationToken ct)
    {
        var db = await GroupAsync(ct);
        var result = await db.StreamAutoClaimAsync(StreamName, options.Value.Group, consumer,
            options.Value.PendingIdleSeconds * 1000L, "0-0", count: 20).WaitAsync(ct);
        return Messages(result.ClaimedEntries);
    }

    public async Task AcknowledgeAsync(string id, CancellationToken ct) =>
        _ = await (await DatabaseAsync(ct)).StreamAcknowledgeAsync(StreamName, options.Value.Group, id).WaitAsync(ct);

    public async Task<bool> ConnectedAsync(CancellationToken ct)
    {
        try { await (await DatabaseAsync(ct)).PingAsync().WaitAsync(ct); return true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RedisException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync();
        connectionLock.Dispose();
    }
}
