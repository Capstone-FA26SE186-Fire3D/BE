using Fire3D.Application.Content;
using Fire3D.Infrastructure.Content;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private sealed class RecordingCacheVersions(Func<string, bool> fail) : IPlatformCacheVersions
    {
        public List<string> Bumped { get; } = [];
        public Task<string> BumpAsync(string scope, CancellationToken ct)
        {
            if (fail(scope)) throw new InvalidOperationException("cache down");
            Bumped.Add(scope);
            return Task.FromResult("test:" + scope);
        }
    }

    // 14/10 Docs review: PlatformCacheInvalidation rows used to stay Pending forever because no consumer claimed them.
    [PostgresFact]
    public async Task Platform_cache_invalidation_events_are_consumed_once_with_receipts_and_backoff()
    {
        Guid learn = Guid.NewGuid(), library = Guid.NewGuid(), bad = Guid.NewGuid();
        await ExecuteAsync($$"""
            SELECT content_invalidate('learn','{{learn}}',1,'{}');SELECT content_invalidate('library','{{library}}',2,'{}');
            SELECT enqueue_integration_outbox_event_internal('weird:{{bad}}','Platform','{{bad}}','PlatformCacheInvalidation','1','{"scope":"weird"}',true,false);
            """);
        var login = "platform_cache_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION platform_cache_invalidation_gate(text,jsonb) TO {login}");
        try
        {
            var runtime = new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = runtime }).Build();
            var cacheDown = true;
            var versions = new RecordingCacheVersions(scope => scope == "library" && cacheDown);
            var worker = new PlatformCacheInvalidationWorker(configuration, versions, Options.Create(new PlatformCacheOptions()), NullLogger<PlatformCacheInvalidationWorker>.Instance);
            async Task<string> Status(string key) => (string)(await ScalarAsync($"SELECT status||'/'||attempts||'/'||COALESCE(last_error,'-') FROM integration_outbox_events WHERE idempotency_key='{key}'"))!;

            Assert.Equal(1, await worker.RunOnceAsync(default));
            Assert.Equal(new[] { "learn" }, versions.Bumped);
            Assert.Equal("Published/1/-", await Status($"learn:{learn}:1"));
            Assert.Equal("Pending/1/CACHE_UNAVAILABLE", await Status($"library:{library}:2"));
            Assert.Equal("Pending/1/SCOPE_UNKNOWN", await Status($"weird:{bad}"));
            Assert.Equal("test:learn", await ScalarAsync($"SELECT result_reference FROM integration_event_consumptions WHERE consumer_name='platform-cache-invalidation' AND event_key='learn:{learn}:1'"));
            // Backoff keeps the failed event out of the next run until it is due.
            Assert.Equal(0, await worker.RunOnceAsync(default));

            cacheDown = false;
            await ExecuteAsync($"UPDATE integration_outbox_events SET available_at=now() WHERE idempotency_key='library:{library}:2'");
            Assert.Equal(1, await worker.RunOnceAsync(default));
            Assert.Equal("Published/2/-", await Status($"library:{library}:2"));
            Assert.Equal(new[] { "learn", "library" }, versions.Bumped);

            // A lease taken over after expiry refuses the old token; completing twice with the current token is a replay.
            await ExecuteAsync($"UPDATE integration_outbox_events SET available_at=now() WHERE idempotency_key='weird:{bad}'");
            await using var connection = new NpgsqlConnection(runtime);
            await connection.OpenAsync();
            async Task<string> Gate(string action, string input)
            {
                await using var command = new NpgsqlCommand("SELECT platform_cache_invalidation_gate(@a,@i::jsonb)::text", connection);
                command.Parameters.AddWithValue("a", action); command.Parameters.AddWithValue("i", input);
                return (string)(await command.ExecuteScalarAsync())!;
            }
            static string Field(string json, string name) => System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty(name).GetString()!;
            var first = Field(await Gate("Claim", "{}"), "leaseToken");
            await ExecuteAsync($"UPDATE integration_outbox_events SET lease_until=now()-interval '1 second' WHERE idempotency_key='weird:{bad}'");
            var second = Field(await Gate("Claim", "{}"), "leaseToken");
            Assert.Equal("LEASE_STALE", Field(await Gate("Complete", $$"""{"eventKey":"weird:{{bad}}","leaseToken":"{{first}}"}"""), "code"));
            Assert.Equal("OK", Field(await Gate("Complete", $$"""{"eventKey":"weird:{{bad}}","leaseToken":"{{second}}","result":"manual"}"""), "code"));
            Assert.Contains("\"replayed\": true", await Gate("Complete", $$"""{"eventKey":"weird:{{bad}}","leaseToken":"{{second}}"}"""));
            Assert.Equal("EMPTY", Field(await Gate("Claim", "{}"), "code"));
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM integration_outbox_events WHERE aggregate_type='Platform' AND status<>'Published'"));
            Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM integration_event_consumptions WHERE consumer_name='platform-cache-invalidation'"));
        }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }
}
