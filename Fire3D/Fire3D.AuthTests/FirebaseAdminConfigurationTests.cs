using Fire3D.API.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class FirebaseAdminConfigurationTests
{
    [Fact]
    public void Uses_the_service_account_json_from_configuration_before_a_local_file()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Firebase:ServiceAccountJson"] = "azure-service-account-json"
        }).Build();

        var credentialJson = FirebaseAdminConfiguration.GetCredentialJson(configuration, "C:\\does-not-matter");

        Assert.Equal("azure-service-account-json", credentialJson);
    }
}
