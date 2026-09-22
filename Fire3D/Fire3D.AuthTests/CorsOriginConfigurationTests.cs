using Fire3D.API.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class CorsOriginConfigurationTests
{
    [Fact]
    public void Uses_multiple_configured_origins_and_normalizes_whitespace_and_trailing_slashes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:FrontendUrls:0"] = " http://localhost:5173/ ",
            ["Auth:FrontendUrls:1"] = "https://web.fire3d.example/"
        }).Build();

        var origins = CorsOriginConfiguration.GetAllowedOrigins(configuration);

        Assert.Equal(["http://localhost:5173", "https://web.fire3d.example"], origins);
    }

    [Fact]
    public void Uses_the_legacy_single_origin_when_the_list_is_not_configured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:FrontendUrl"] = "https://legacy.fire3d.example"
        }).Build();

        Assert.Equal(["https://legacy.fire3d.example"], CorsOriginConfiguration.GetAllowedOrigins(configuration));
    }
}
