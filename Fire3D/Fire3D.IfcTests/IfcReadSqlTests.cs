using Fire3D.Infrastructure.Persistence;
using Fire3D.Infrastructure.Ifc;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.NameTranslation;
using Xunit;
namespace Fire3D.IfcTests;

public sealed class IfcPostgresFactAttribute : FactAttribute
{
    public IfcPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRE3D_IFC_TEST_CONNECTION")))
            Skip = "Set FIRE3D_IFC_TEST_CONNECTION to a local PostgreSQL server with CREATEDB.";
    }
}

public sealed class IfcReadDatabase : IAsyncLifetime
{
    private readonly string name = "fire3d_ifc_test_" + Guid.NewGuid().ToString("N");
    private string? adminConnection;
    private string? connection;
    private bool created;
    public readonly Guid Tenant = Guid.NewGuid(), OtherTenant = Guid.NewGuid(),
        Building = Guid.NewGuid(), Revision = Guid.NewGuid(), Job = Guid.NewGuid(), Attempt = Guid.NewGuid();
    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("FIRE3D_IFC_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var builder = new NpgsqlConnectionStringBuilder(configured)
        { Database = "postgres", Pooling = false, IncludeErrorDetail = false };
        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("IFC tests require a local PostgreSQL server.");
        adminConnection = builder.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {name}", admin).ExecuteNonQueryAsync();
            created = true;
        }
        builder.Database = name; connection = builder.ConnectionString;
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        Assert.Equal(name, db.Database);
        // A read-contract fixture, not a claim that all production constraints/migrations passed.
        var ddl = """
            CREATE TYPE revision_status_enum AS ENUM ('Draft','Uploaded','Processing','NeedsFix','ReadyForScenario','ConfirmedForTraining','Rejected','Failed','Superseded');
            CREATE TYPE quarantine_status_enum AS ENUM ('Pending','Accepted','Rejected');
            CREATE TABLE organizations(id uuid primary key,is_active boolean not null,deleted_at timestamptz);
            CREATE TABLE buildings(id uuid primary key,organization_id uuid,is_active boolean,deleted_at timestamptz);
            CREATE TABLE revisions(id uuid primary key,building_id uuid,version_label text,status revision_status_enum,created_at timestamptz);
            CREATE TABLE source_documents(id uuid primary key,revision_id uuid,original_filename text,file_size_bytes bigint,quarantine_status quarantine_status_enum,created_at timestamptz);
            CREATE TABLE processing_jobs(id uuid primary key,revision_id uuid,source_document_id uuid,scenario_version_id uuid,kind text,status text,created_at timestamptz,input_hash text,current_attempt_id uuid);
            CREATE TABLE processing_job_attempts(id uuid primary key,processing_job_id uuid,input_hash text,attempt_number int,status text,toolchain_version text,started_at timestamptz,finished_at timestamptz,output_hash text);
            """;
        await new NpgsqlCommand(ddl,db).ExecuteNonQueryAsync();
        await new NpgsqlCommand($"""
            INSERT INTO organizations VALUES ('{Tenant}',true,null),('{OtherTenant}',true,null);
            INSERT INTO buildings VALUES ('{Building}','{Tenant}',true,null);
            INSERT INTO revisions VALUES ('{Revision}','{Building}','v1','Processing',now());
            INSERT INTO processing_jobs VALUES ('{Job}','{Revision}','{Guid.NewGuid()}',null,'Geometry','Running',now(),'{new string('a',64)}','{Attempt}');
            INSERT INTO processing_job_attempts VALUES ('{Attempt}','{Job}','{new string('a',64)}',1,'Running','test-1',now(),null,null);
            """,db).ExecuteNonQueryAsync();
    }
    public Fire3DDbContext Context()
    {
        if(connection is null) throw new InvalidOperationException("Test database not initialized.");
        var options = new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(connection, pg =>
        {
            pg.MapEnum<RevisionStatus>("revision_status_enum",nameTranslator:new NpgsqlNullNameTranslator());
            pg.MapEnum<QuarantineStatus>("quarantine_status_enum",nameTranslator:new NpgsqlNullNameTranslator());
        }).Options;
        var context = new Fire3DDbContext(options);
        Assert.Equal(name,context.Database.GetDbConnection().Database);
        return context;
    }
    public async Task DisposeAsync()
    {
        if(!created) return;
        await using var admin=new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)",admin).ExecuteNonQueryAsync();
    }
}

public sealed class IfcReadSqlTests(IfcReadDatabase database) : IClassFixture<IfcReadDatabase>
{
    [IfcPostgresFact]
    public async Task Job_detail_joins_current_attempt_and_hides_other_tenant()
    {
        await using var db=database.Context();
        var store=new IfcReadStore(db);
        var own=await store.GetJobAsync(database.Job,database.Tenant,default);
        Assert.NotNull(own);
        Assert.Equal(database.Attempt,own.CurrentAttempt?.Id);
        Assert.Equal(1,own.CurrentAttempt?.AttemptNumber);
        Assert.Null(await store.GetJobAsync(database.Job,database.OtherTenant,default));
        Assert.NotNull(await store.GetJobAsync(database.Job,null,default));
        Assert.Null(await store.GetJobAsync(Guid.NewGuid(),database.Tenant,default));
    }
    [IfcPostgresFact]
    public async Task Job_list_is_paged_and_tenant_scoped_in_SQL()
    {
        await using var db=database.Context();
        var store=new IfcReadStore(db);
        var first=await store.ListJobsAsync(database.Revision,database.Tenant,1,1,default);
        Assert.NotNull(first); Assert.Equal(database.Job,Assert.Single(first.Items).Id);
        var second=await store.ListJobsAsync(database.Revision,database.Tenant,2,1,default);
        Assert.NotNull(second); Assert.Empty(second.Items); Assert.Equal(1,second.TotalCount);
        Assert.Null(await store.ListJobsAsync(database.Revision,database.OtherTenant,1,20,default));
    }
    [IfcPostgresFact]
    public async Task Revision_detail_and_list_work_without_revision_tenant_column()
    {
        await using var db=database.Context();
        var store=new Fire3D.Infrastructure.Buildings.BuildingStore(db);
        Assert.NotNull(await store.FindRevisionAsync(database.Revision,database.Tenant,default));
        Assert.Null(await store.FindRevisionAsync(database.Revision,database.OtherTenant,default));
        var list=await store.ListRevisionsAsync(database.Building,database.Tenant,1,20,default);
        Assert.Equal(database.Revision,Assert.Single(list.Items).Id);
        Assert.False(await store.RevisionBuildingExistsAsync(database.Building,database.OtherTenant,default));
    }
}
