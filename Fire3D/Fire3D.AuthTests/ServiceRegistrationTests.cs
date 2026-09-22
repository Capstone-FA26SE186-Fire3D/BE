using Fire3D.API.Extensions;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

public class ServiceRegistrationTests
{
    [Fact]
    public void Authentication_handlers_have_registered_dependencies()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=unused;Username=test",
            ["AWS:Region"] = "ap-southeast-1",
            ["Jwt:Issuer"] = "Fire3D.Tests", ["Jwt:Audience"] = "Fire3D.Tests",
            ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[64])
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDatabase(config);
        services.AddApplication(config);
        services.AddAccountAuthentication(config);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIdentityProvider>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Fire3DSessionIssuer>());
    }
}
