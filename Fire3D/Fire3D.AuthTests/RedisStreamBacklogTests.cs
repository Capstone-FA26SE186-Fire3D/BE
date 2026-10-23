using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Content;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>Real disposable Redis: backlog, retention cursor, cancellation and the platform cache version counter.</summary>
public sealed class RedisStreamBacklogTests
{
    private static RedisProcessingStream Stream(RedisProcessingOptions options) =>
        new(Options.Create(options), RedisStreamIntegrationTests.TestEnvironment("test-" + Guid.NewGuid().ToString("N")));

    [RedisFact]
    public async Task Backlog_reports_stream_length_and_unacknowledged_entries_of_the_group()
    {
        var options = RedisStreamIntegrationTests.TestOptions();
        await using var stream = Stream(options);
        await using var connection = await RedisStreamIntegrationTests.ConnectAsync(options);
        try
        {
            Assert.Equal(new ProcessingStreamBacklog(0, 0), await stream.BacklogAsync(default));
            for (var i = 0; i < 3; i++) await stream.PublishAsync(RedisStreamIntegrationTests.Envelope(), default);
            var read = await stream.ReadAsync("reader", default);
            Assert.Equal(3, read.Count);
            await stream.AcknowledgeAsync(read[1].Id, default);
            Assert.Equal(new ProcessingStreamBacklog(3, 2), await stream.BacklogAsync(default));
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(stream.StreamName); }
    }

    [RedisFact]
    public async Task Retention_scan_continues_after_a_full_batch_then_restarts_from_the_oldest_entry()
    {
        var options = RedisStreamIntegrationTests.TestOptions();
        await using var stream = Stream(options);
        await using var connection = await RedisStreamIntegrationTests.ConnectAsync(options);
        var db = connection.GetDatabase();
        try
        {
            var old = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds();
            var envelope = JsonSerializer.Serialize(RedisStreamIntegrationTests.Envelope(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            for (var i = 0; i < 101; i++) await db.StreamAddAsync(stream.StreamName, [new NameValueEntry("envelope", envelope)], $"{old}-{i}");
            async Task<List<string>> Scan()
            {
                var seen = new List<string>();
                // Nothing is confirmed as handed off, so the scan only moves its cursor and deletes nothing.
                Assert.Equal(0, await stream.TrimAsync((m, _) => { seen.Add(m.Id); return Task.FromResult(false); }, default));
                return seen;
            }
            var first = await Scan();
            Assert.Equal(100, first.Count);
            Assert.Equal($"{old}-0", first[0]);
            Assert.Equal([$"{old}-100"], await Scan());
            Assert.Equal(first, await Scan());
            Assert.Equal(101, await db.StreamLengthAsync(stream.StreamName));
        }
        finally { await db.KeyDeleteAsync(stream.StreamName); }
    }

    [RedisFact]
    public async Task Connection_check_propagates_caller_cancellation()
    {
        await using var stream = Stream(RedisStreamIntegrationTests.TestOptions());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ConnectedAsync(cancelled.Token));
        Assert.True(await stream.ConnectedAsync(default));
    }

    [RedisFact]
    public async Task Platform_cache_versions_increment_per_scope_and_environment()
    {
        var options = RedisStreamIntegrationTests.TestOptions();
        var environment = "test-" + Guid.NewGuid().ToString("N");
        await using var versions = new RedisPlatformCacheVersions(Options.Create(options), RedisStreamIntegrationTests.TestEnvironment(environment));
        await using var connection = await RedisStreamIntegrationTests.ConnectAsync(options);
        var db = connection.GetDatabase();
        try
        {
            Assert.Equal("redis:learn:1", await versions.BumpAsync("learn", default));
            Assert.Equal("redis:learn:2", await versions.BumpAsync("learn", default));
            Assert.Equal("redis:library:1", await versions.BumpAsync("library", default));
            Assert.Equal(2, (int)await db.StringGetAsync($"fet3d:{environment}:cache:learn:version"));
        }
        finally { await db.KeyDeleteAsync([$"fet3d:{environment}:cache:learn:version", $"fet3d:{environment}:cache:library:version"]); }
    }

    [Fact]
    public async Task Platform_cache_versions_without_Redis_invalidate_nothing()
    {
        await using var versions = new RedisPlatformCacheVersions(Options.Create(new RedisProcessingOptions { Enabled = false }), RedisStreamIntegrationTests.TestEnvironment("unused"));
        Assert.Equal("no-cache", await versions.BumpAsync("learn", default));
    }
}
