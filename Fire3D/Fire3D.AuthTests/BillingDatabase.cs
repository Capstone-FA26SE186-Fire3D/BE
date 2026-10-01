using Fire3D.API.Extensions;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingPostgresFactAttribute : FactAttribute
{
    public BillingPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FET3D_BILLING_TEST_ADMIN")))
            Skip = "Set FET3D_BILLING_TEST_ADMIN to a disposable loopback PostgreSQL server.";
    }
}

internal sealed class BillingDatabase : IAsyncDisposable
{
    private readonly string admin;
    private readonly string name = "fet3d_billing_test_" + Guid.NewGuid().ToString("N");
    public string Connection { get; private set; } = "";
    public static readonly Guid Admin = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Owner = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid Other = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid Org = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid OtherOrg = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid Building = Guid.Parse("30000000-0000-0000-0000-000000000001");

    private BillingDatabase(string admin) => this.admin = admin;
    public static async Task<BillingDatabase> Create(bool applyBilling = true)
    {
        var source = Environment.GetEnvironmentVariable("FET3D_BILLING_TEST_ADMIN")
            ?? throw new InvalidOperationException("An explicit test connection is required.");
        var settings = new NpgsqlConnectionStringBuilder(source);
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Database != "postgres")
            throw new InvalidOperationException("Billing tests require loopback and the postgres admin database.");
        var result = new BillingDatabase(source);
        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE {result.name}", connection).ExecuteNonQueryAsync();
        settings.Database = result.name;
        result.Connection = settings.ConnectionString;
        try
        {
            await using var db = result.Context();
            Assert.Equal(result.name, db.Database.GetDbConnection().Database);
            await db.Database.EnsureCreatedAsync();
            if (applyBilling) await result.ApplyBilling();
            await result.Sql($$"""
                INSERT INTO organizations(id,name,slug,plan,is_active,metadata,created_at,updated_at)
                VALUES ('{{Org}}','One','one','Standard',true,'{}',now(),now()),
                       ('{{OtherOrg}}','Two','two','Standard',true,'{}',now(),now());
                INSERT INTO users(id,email,role,organization_id,is_active,created_at,updated_at)
                VALUES ('{{Admin}}','admin@example.test','PlatformAdmin',null,true,now(),now()),
                       ('{{Owner}}','owner@example.test','OrganizationUser','{{Org}}',true,now(),now()),
                       ('{{Other}}','other@example.test','OrganizationUser','{{OtherOrg}}',true,now(),now());
                INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at)
                VALUES ('{{Building}}','{{Org}}','Building One',true,'{{Owner}}',now(),now());
                INSERT INTO building_locations(building_id,address,created_at,updated_at)
                VALUES ('{{Building}}','1 Example Street',now(),now());
                """);
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public Fire3DDbContext Context()
    {
        var services = new ServiceCollection();
        services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["ConnectionStrings:DefaultConnection"] = Connection }).Build());
        return new Fire3DDbContext(services.BuildServiceProvider().GetRequiredService<DbContextOptions<Fire3DDbContext>>());
    }
    public async Task ApplyBilling()
    {
        var migration = new Fire3D.Infrastructure.Migrations.AddBuildingBilling();
        foreach (var operation in migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
            await Sql(operation.Sql);
    }
    public async Task Sql(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await new NpgsqlCommand(sql,connection) { CommandTimeout = 30 }.ExecuteNonQueryAsync();
    }
    public async Task<object?> Scalar(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        return await new NpgsqlCommand(sql,connection).ExecuteScalarAsync();
    }
    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)",connection).ExecuteNonQueryAsync();
    }
}
