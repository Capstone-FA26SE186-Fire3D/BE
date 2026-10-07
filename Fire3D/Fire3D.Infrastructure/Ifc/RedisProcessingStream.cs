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
    private string reclaimCursor = "0-0";
    private string retentionCursor = "-";
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
            options.Value.PendingIdleSeconds * 1000L, reclaimCursor, count: 20).WaitAsync(ct);
        reclaimCursor = result.NextStartId.ToString();
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

    public async Task<ProcessingStreamBacklog> BacklogAsync(CancellationToken ct)
    {
        var db = await GroupAsync(ct);
        var pending = await db.StreamPendingAsync(StreamName, options.Value.Group).WaitAsync(ct);
        return new(await db.StreamLengthAsync(StreamName).WaitAsync(ct), pending.PendingMessageCount);
    }

    // XDEL is conditional per entry. Never XTRIM/expire the stream across pending or unread work.
    public async Task<int> TrimAsync(Func<ProcessingStreamMessage, CancellationToken, Task<bool>> handedOff, CancellationToken ct)
    {
        var db = await GroupAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays).ToUnixTimeMilliseconds();
        var entries = await db.StreamRangeAsync(StreamName, retentionCursor, $"{cutoff}-0", 100).WaitAsync(ct);
        var removed = 0;
        foreach (var entry in Messages(entries))
        {
            if (await handedOff(entry, ct))
                removed += (int)(long)await db.ScriptEvaluateAsync(SafeDelete, [StreamName], [options.Value.Group, entry.Id, cutoff]).WaitAsync(ct);
        }
        // Inclusive XRANGE: increment the last sequence to continue past protected entries.
        if (entries.Length < 100) retentionCursor = "-";
        else
        {
            var parts = entries[^1].Id.ToString().Split('-');
            retentionCursor = $"{parts[0]}-{ulong.Parse(parts[1]) + 1}";
        }
        return removed;
    }

    private const string SafeDelete = """
        local function less(a,b)
          local am,as=string.match(a,'^(%d+)%-(%d+)$')
          local bm,bs=string.match(b,'^(%d+)%-(%d+)$')
          if #am~=#bm then return #am<#bm end
          if am~=bm then return am<bm end
          if #as~=#bs then return #as<#bs end
          return as<bs
        end
        local groups=redis.call('XINFO','GROUPS',KEYS[1])
        if #groups~=1 then return 0 end
        local name,last
        for i=1,#groups[1],2 do
          if groups[1][i]=='name' then name=groups[1][i+1] end
          if groups[1][i]=='last-delivered-id' then last=groups[1][i+1] end
        end
        if name~=ARGV[1] or not last or less(last,ARGV[2]) then return 0 end
        if not less(ARGV[2],ARGV[3]..'-0') then return 0 end
        if #redis.call('XPENDING',KEYS[1],ARGV[1],ARGV[2],ARGV[2],1)>0 then return 0 end
        return redis.call('XDEL',KEYS[1],ARGV[2])
        """;

    public async ValueTask DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync();
        connectionLock.Dispose();
    }
}
