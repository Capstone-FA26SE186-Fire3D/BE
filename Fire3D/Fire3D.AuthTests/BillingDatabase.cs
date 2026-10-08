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
    public string RequestConnection { get; private set; } = "";
    public string WebhookConnection { get; private set; } = "";
    private string requestLogin="",webhookLogin="";
    private string migrationLogin="";
    public static readonly Guid Admin = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Owner = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid Other = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid Org = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid OtherOrg = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid Building = Guid.Parse("30000000-0000-0000-0000-000000000001");

    private BillingDatabase(string admin) => this.admin = admin;
    public static async Task<BillingDatabase> Create(bool applyBilling = true,bool applyPayos = true,bool migrationHistory=false)
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
            if(migrationHistory)
            {
                await using var migrationDb=new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(result.Connection).Options);
                await migrationDb.Database.MigrateAsync();
            }
            else
            {
                await db.Database.EnsureCreatedAsync();
                if (applyBilling)
                {
                    await result.ApplyBilling();
                    await result.Sql("DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN CREATE ROLE fire3d_api NOLOGIN; END IF; END $$;");
                    foreach (var operation in new Fire3D.Infrastructure.Migrations.AddBillingV7Catalog().UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
                        await result.Sql(operation.Sql);
                    if(applyPayos)await result.ApplyPayos();
                    foreach (var operation in new Fire3D.Infrastructure.Migrations.AddBillingV7QuotationSnapshots().UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
                        await result.Sql(operation.Sql);
                }
            }
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
                INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at)
                SELECT gen_random_uuid(),id,id,id::text,now(),now()+interval '1 day' FROM users;
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
        foreach (var operation in new Fire3D.Infrastructure.Migrations.AddBillingCatalogRevisions().UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
            await Sql(operation.Sql);
    }
    public async Task ApplyPayos()
    {
        foreach(var resource in new[]{"PayosRuntime.sql","PayosCheckout.sql","PayosWebhook.sql","PayosProvisioning.sql"})
        {
            using var stream=typeof(Fire3D.Infrastructure.Billing.PayosSdkProvider).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing."+resource);
            if(stream is null)throw new InvalidOperationException("Missing runtime migration resource: "+resource);
            await Sql(new StreamReader(stream).ReadToEnd());
        }
        requestLogin="test_req_"+Guid.NewGuid().ToString("N");webhookLogin="test_hook_"+Guid.NewGuid().ToString("N");
        await Sql($"CREATE ROLE {requestLogin} LOGIN NOINHERIT; CREATE ROLE {webhookLogin} LOGIN NOINHERIT; GRANT fet3d_payos_request_executor TO {requestLogin}; GRANT fet3d_payos_webhook_executor TO {webhookLogin};");
        var settings=new NpgsqlConnectionStringBuilder(Connection){Username=requestLogin};RequestConnection=settings.ConnectionString;
        settings.Username=webhookLogin;WebhookConnection=settings.ConnectionString;
    }
    public async Task ApplyPayosAsRestrictedMigration()
    {
        migrationLogin="test_migration_"+Guid.NewGuid().ToString("N");
        await Sql($$"""
            CREATE ROLE {{migrationLogin}} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
            GRANT fet3d_payos_ledger_owner TO {{migrationLogin}};
            GRANT USAGE,CREATE ON SCHEMA public TO {{migrationLogin}};
            ALTER SCHEMA public OWNER TO {{migrationLogin}};
            DO $transfer$ DECLARE obj record; BEGIN
              FOR obj IN SELECT relname FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r'
                AND relname NOT IN('payment_transactions','payos_payment_requests') LOOP
                EXECUTE format('ALTER TABLE public.%I OWNER TO {{migrationLogin}}',obj.relname);
              END LOOP;
              FOR obj IN SELECT p.oid::regprocedure signature FROM pg_proc p WHERE p.pronamespace='public'::regnamespace
                AND p.proowner<>(SELECT oid FROM pg_roles WHERE rolname='fet3d_payos_ledger_owner')
                AND NOT EXISTS(SELECT 1 FROM pg_depend d WHERE d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.deptype='e') LOOP
                EXECUTE format('ALTER FUNCTION %s OWNER TO {{migrationLogin}}',obj.signature);
              END LOOP;
            END $transfer$;
            """);
        await using var connection=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Connection){Username=migrationLogin}.ConnectionString);
        await connection.OpenAsync();await using var tx=await connection.BeginTransactionAsync();
        foreach(var resource in new[]{"PayosRuntime.sql","PayosCheckout.sql","PayosWebhook.sql","PayosProvisioning.sql"})
        {
            using var stream=typeof(Fire3D.Infrastructure.Billing.PayosSdkProvider).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing."+resource)!;
            await new NpgsqlCommand(new StreamReader(stream).ReadToEnd(),connection,tx).ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
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
        foreach(var role in new[]{requestLogin,webhookLogin,migrationLogin}.Where(x=>x.Length>0))await new NpgsqlCommand($"DROP ROLE IF EXISTS {role}",connection).ExecuteNonQueryAsync();
    }
}
