using Fire3D.API.Extensions;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class RedisProcessingOptionsTests
{
    private static ServiceProvider Provider(Dictionary<string, string?> settings, string environment = "Production")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddApplication(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Disabled_defaults_do_not_require_Redis_or_change_HTTP_transport()
    {
        using var provider = Provider([]);
        Assert.False(provider.GetRequiredService<IOptions<RedisProcessingOptions>>().Value.Enabled);
        Assert.Equal("Http", provider.GetRequiredService<IOptions<ProcessingWorkerOptions>>().Value.Transport);
    }

    [Theory]
    [InlineData("Production", "false", "test-password", false)]
    [InlineData("Development", "false", "test-password", true)]
    [InlineData("Production", "true", "", false)]
    [InlineData("Production", "true", "test-password", true)]
    public void Enabled_connection_requires_credentials_and_production_TLS(string environment, string ssl, string password, bool valid)
    {
        using var provider = Provider(new() { ["Redis:Enabled"] = "true", ["Redis:Host"] = "localhost", ["Redis:Port"] = "6379", ["Redis:Ssl"] = ssl, ["Redis:Password"] = password }, environment);
        if (valid) Assert.True(provider.GetRequiredService<IOptions<RedisProcessingOptions>>().Value.Enabled);
        else Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RedisProcessingOptions>>().Value);
    }

    [Fact]
    public void Redis_publisher_does_not_require_HTTP_worker_but_consumer_does()
    {
        var settings = new Dictionary<string, string?> { ["Redis:Enabled"] = "true", ["ProcessingWorker:Transport"] = "RedisStreams", ["ProcessingWorker:DispatcherEnabled"] = "true", ["ConnectionStrings:DispatcherExecutor"] = "Host=localhost;Database=test" };
        using (var publisher = Provider(settings)) Assert.Equal("RedisStreams", publisher.GetRequiredService<IOptions<ProcessingWorkerOptions>>().Value.Transport);
        settings["ProcessingWorker:ConsumerEnabled"] = "true";
        using var consumer = Provider(settings);
        Assert.Throws<OptionsValidationException>(() => consumer.GetRequiredService<IOptions<ProcessingWorkerOptions>>().Value);
    }

    [Theory]
    [InlineData("Redis", "false")]
    [InlineData("RedisStreams", "false")]
    public void Invalid_or_disabled_transport_is_rejected(string transport, string redisEnabled)
    {
        using var provider = Provider(new() { ["ProcessingWorker:Transport"] = transport, ["ProcessingWorker:DispatcherEnabled"] = "true", ["Redis:Enabled"] = redisEnabled });
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ProcessingWorkerOptions>>().Value);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "RedisTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
