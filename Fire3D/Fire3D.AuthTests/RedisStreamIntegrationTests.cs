using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("FIRE3D_TEST_REDIS_PORT"), out _))
            Skip = "Set FIRE3D_TEST_REDIS_PORT for the loopback disposable Redis with test credentials.";
    }
}

public sealed class RedisPostgresFactAttribute : FactAttribute
{
    public RedisPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIRE3D_TEST_ADMIN_CONNECTION")) ||
            !int.TryParse(Environment.GetEnvironmentVariable("FIRE3D_TEST_REDIS_PORT"), out _))
            Skip = "Set disposable loopback PostgreSQL and Redis test environment variables.";
    }
}

public sealed class RedisStreamIntegrationTests
{
    internal static RedisProcessingOptions TestOptions() => new()
    {
        Enabled = true, Host = "127.0.0.1", Port = int.Parse(Environment.GetEnvironmentVariable("FIRE3D_TEST_REDIS_PORT")!),
        Ssl = false, Password = "fet3d-disposable-test"
    };
    internal static IHostEnvironment TestEnvironment(string name) => ResetProxy.For<IHostEnvironment>((method, _) => method == "get_EnvironmentName" ? name : null);
    internal static ProcessingEnvelope Envelope() => new("test:" + Guid.NewGuid().ToString("N"), "ProcessingJobRequested", "1", "ProcessingJob", Guid.NewGuid(), Guid.NewGuid(), JsonSerializer.SerializeToElement(new { job_id = Guid.NewGuid() }), new string('a', 64));
    internal static Task<ConnectionMultiplexer> ConnectAsync(RedisProcessingOptions options) => ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
    {
        EndPoints = { { options.Host, options.Port } }, Password = options.Password, AbortOnConnectFail = true
    });

    [RedisFact]
    public async Task Redis_real_XADD_stores_envelope_without_dispatch_or_processing_lease()
    {
        var options = TestOptions();
        await using var stream = new RedisProcessingStream(Options.Create(options), TestEnvironment("test-" + Guid.NewGuid().ToString("N")));
        await using var connection = await ConnectAsync(options);
        try
        {
            Assert.True(await stream.ConnectedAsync(default));
            var expected = Envelope();
            var id = await stream.PublishAsync(expected, default);
            var stored = Assert.Single(await connection.GetDatabase().StreamRangeAsync(stream.StreamName));
            Assert.Equal(id, stored.Id.ToString());
            var json = stored.Values.Single().Value.ToString();
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(expected.EventKey, parsed.RootElement.GetProperty("eventKey").GetString());
            Assert.Equal(expected.OrganizationId, parsed.RootElement.GetProperty("organizationId").GetGuid());
            Assert.DoesNotContain("leaseToken", json); Assert.DoesNotContain("Password", json);
        }
        finally { await connection.GetDatabase().KeyDeleteAsync(stream.StreamName); }
    }
}
