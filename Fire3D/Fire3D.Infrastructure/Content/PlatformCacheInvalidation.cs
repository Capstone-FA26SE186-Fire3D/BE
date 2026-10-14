using System.Text.Json;
using Fire3D.Application.Content;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Fire3D.Infrastructure.Content;

/// <summary>Redis INCR of fet3d:{environment}:cache:{scope}:version. Without Redis there is no shared cache, so nothing is invalidated.</summary>
public sealed class RedisPlatformCacheVersions(IOptions<RedisProcessingOptions> options, IHostEnvironment environment) : IPlatformCacheVersions, IAsyncDisposable
{
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private ConnectionMultiplexer? connection;

    public async Task<string> BumpAsync(string scope, CancellationToken ct)
    {
        if (!options.Value.Enabled) return "no-cache";
        if (connection is null)
        {
            await connectionLock.WaitAsync(ct);
            try
            {
                if (connection is null)
                {
                    var config = new ConfigurationOptions
                    {
                        Ssl = options.Value.Ssl, Password = options.Value.Password, AbortOnConnectFail = false, ConnectTimeout = 5000,
                        AsyncTimeout = 5000, BacklogPolicy = BacklogPolicy.FailFast, ClientName = "fet3d-platform-cache"
                    };
                    config.EndPoints.Add(options.Value.Host, options.Value.Port);
                    connection = await ConnectionMultiplexer.ConnectAsync(config);
                }
            }
            finally { connectionLock.Release(); }
        }
        var version = await connection.GetDatabase().StringIncrementAsync($"fet3d:{environment.EnvironmentName.ToLowerInvariant()}:cache:{scope}:version");
        return $"redis:{scope}:{version}";
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync();
        connectionLock.Dispose();
    }
}

/// <summary>
/// Consumes PlatformCacheInvalidation outbox events written with Learn/Library/knowledge changes: claim with a lease, bump the scope
/// version, then record the receipt and close the event. Failures back off in PostgreSQL and stop after ten attempts (status Failed).
/// </summary>
public sealed class PlatformCacheInvalidationWorker(
    IConfiguration configuration,
    IPlatformCacheVersions versions,
    IOptions<PlatformCacheOptions> configured,
    ILogger<PlatformCacheInvalidationWorker> logger) : BackgroundService
{
    private static readonly string[] Scopes = ["learn", "library", "knowledge"];
    private readonly PlatformCacheOptions options = configured.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled) { logger.LogInformation("Platform cache invalidation worker is disabled."); return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Platform cache invalidation run failed."); }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>Handles due events until none is left or MaxEventsPerRun is reached; returns how many were completed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var completed = 0;
        for (var i = 0; i < options.MaxEventsPerRun; i++)
        {
            var claim = await GateAsync("Claim", new { }, ct);
            if (claim.GetProperty("code").GetString() != "OK") break;
            var key = claim.GetProperty("eventKey").GetString()!;
            var token = claim.GetProperty("leaseToken").GetString()!;
            var scope = claim.GetProperty("payload").TryGetProperty("scope", out var s) ? s.GetString() : null;
            string? result = null, error = null;
            if (scope is null || !Scopes.Contains(scope)) error = "SCOPE_UNKNOWN";
            else
            {
                try { result = await versions.BumpAsync(scope, ct); }
                catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Cache invalidation for {EventKey} failed; it will be retried.", key);
                    error = "CACHE_UNAVAILABLE";
                }
            }
            var outcome = await GateAsync(error is null ? "Complete" : "Fail", new { eventKey = key, leaseToken = token, result, error }, ct);
            if (error is null && outcome.GetProperty("code").GetString() == "OK") completed++;
        }
        return completed;
    }

    private async Task<JsonElement> GateAsync(string action, object input, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT platform_cache_invalidation_gate(@action,@input)::text", connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(input));
        using var json = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        return json.RootElement.Clone();
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
