using Fire3D.API.Extensions;
using Fire3D.Application.Billing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class PayosConfigurationTests
{
    private static ServiceProvider Services(bool enabled, string returnUrl="https://api.example.test/billing/payment-return/", string apiKey="test-api-key")
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["PayOS:Enabled"]=enabled.ToString(), ["PayOS:ClientId"]="test-client",
            ["PayOS:ApiKey"]=apiKey, ["PayOS:ChecksumKey"]="test-checksum",
            ["PayOS:ReturnUrl"]=returnUrl, ["PayOS:CancelUrl"]="https://api.example.test/billing/payment-cancel/",
            ["ConnectionStrings:PayosRequestExecutor"]="Host=127.0.0.1;Database=isolated;Username=request_executor",
            ["ConnectionStrings:PayosWebhookExecutor"]="Host=127.0.0.1;Database=isolated;Username=webhook_executor"
        }).Build();
        var services=new ServiceCollection(); services.AddLogging(); services.AddApplication(configuration);
        return services.BuildServiceProvider();
    }
    [Fact]
    public void Complete_enabled_configuration_is_accepted_without_provider_call()
    {
        using var services=Services(true);
        Assert.True(services.GetRequiredService<IOptions<PayosOptions>>().Value.Enabled);
    }
    [Fact]
    public void Enabled_configuration_rejects_http_callback_and_missing_credential()
    {
        using var http=Services(true,"http://localhost:3000");
        Assert.Throws<OptionsValidationException>(()=>http.GetRequiredService<IOptions<PayosOptions>>().Value);
        using var missing=Services(true,apiKey:"");
        Assert.Throws<OptionsValidationException>(()=>missing.GetRequiredService<IOptions<PayosOptions>>().Value);
    }
    [Fact]
    public void Disabled_checkout_allows_deployment_without_credentials()
    {
        using var services=Services(false,apiKey:"");
        Assert.False(services.GetRequiredService<IOptions<PayosOptions>>().Value.Enabled);
    }
}
