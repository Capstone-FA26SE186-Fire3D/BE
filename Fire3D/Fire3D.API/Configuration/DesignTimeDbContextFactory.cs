using Fire3D.API.Extensions;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fire3D.API.Configuration;

/// <summary>Model tooling must not start workers, read credentials or connect the application database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<Fire3DDbContext>
{
    private ServiceProvider? provider;
    public Fire3DDbContext CreateDbContext(string[] args)
    {
        var services = new ServiceCollection();
        services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=fet3d_design_only;Username=design_only"
        }).Build());
        // The options reference the provider's Npgsql data source; it must outlive the context.
        provider = services.BuildServiceProvider();
        return provider.GetRequiredService<Fire3DDbContext>();
    }
}
