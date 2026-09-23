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
    public async Task Storage_uses_bucket_from_aws_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AWS:Region"] = "ap-northeast-1",
            ["AWS:BucketName"] = "fire3d-config-test-bucket"
        }).Build();
        var services = new ServiceCollection();
        services.AddApplication(config);

        var previousAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var previousSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIAIOSFODNN7EXAMPLE");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        try
        {
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<Fire3D.Application.Storage.IStorageService>();

            var url = await storage.GeneratePresignedUploadUrlAsync("test.ifc", "application/octet-stream", TimeSpan.FromMinutes(1), CancellationToken.None);

            Assert.Contains("fire3d-config-test-bucket", url, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousAccessKey);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecretKey);
        }
    }

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
