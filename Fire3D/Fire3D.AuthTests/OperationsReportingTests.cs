using System.Data.Common;
using System.Net;
using System.Text.Json;
using Fire3D.API.Extensions;
using Fire3D.Infrastructure.Persistence;
using Fire3D.Infrastructure.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class OperationsReportingTests
{
    [BillingPostgresFact]
    public async Task Operations_HTTP_tenant_empty_buckets_lifecycle_and_time_boundaries_are_correct()
    {
        await using var database=await BillingDatabase.Create(migrationHistory:true);using var factory=new BillingApiTests.Factory(database);
        await database.Sql($$"""
            INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at,deleted_at)
            VALUES(gen_random_uuid(),'{{BillingDatabase.Org}}','Historical',false,'{{BillingDatabase.Owner}}','2026-10-01T00:00:00Z',now(),now()),
            (gen_random_uuid(),'{{BillingDatabase.OtherOrg}}','Other tenant',true,'{{BillingDatabase.Other}}','2026-10-02T00:00:00Z',now(),null);
            INSERT INTO support_tickets(id,ticket_number,created_by,organization_id,subject,description,status,priority,created_at,updated_at)
            VALUES(gen_random_uuid(),'T1','{{BillingDatabase.Owner}}','{{BillingDatabase.Org}}','Fixture','Fixture','Open','Normal','2026-10-01T00:00:00Z',now()),
            (gen_random_uuid(),'T2','{{BillingDatabase.Other}}','{{BillingDatabase.OtherOrg}}','Fixture','Fixture','Closed','Normal','2026-10-03T00:00:00Z',now());
            """);
        using var owner=factory.As(BillingDatabase.Owner);using var admin=factory.As(BillingDatabase.Admin);
        const string range="?from=2026-10-01T00:00:00Z&to=2026-10-03T00:00:00Z";
        var response=await owner.GetAsync("/api/organizations/me/analytics/operations"+range);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var result=json.RootElement;
        Assert.False(result.TryGetProperty("accounts",out _));Assert.Equal(1,result.GetProperty("created").GetProperty("tickets").GetInt32());
        Assert.DoesNotContain("Accounts",result.GetProperty("definitions").GetProperty("snapshot").GetString());
        Assert.Equal(1,result.GetProperty("created").GetProperty("buildings").GetInt32()); // retained historical deleted record at lower bound
        Assert.Equal(1,result.GetProperty("buildings").EnumerateArray().Sum(x=>x.GetProperty("count").GetInt32()));
        Assert.All(result.GetProperty("processingJobs").EnumerateArray(),x=>Assert.Equal(0,x.GetProperty("count").GetInt32()));
        using var platform=JsonDocument.Parse(await (await admin.GetAsync("/api/admin/analytics/operations"+range)).Content.ReadAsStringAsync());
        Assert.Equal(3,platform.RootElement.GetProperty("accounts").EnumerateArray().Sum(x=>x.GetProperty("count").GetInt32()));
        Assert.Equal(1,platform.RootElement.GetProperty("created").GetProperty("tickets").GetInt32()); // upper bound excluded
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.GetAsync("/api/admin/analytics/operations")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.GetAsync("/api/organizations/me/analytics/operations?from=2026-10-01T00:00:00")).StatusCode);
        await database.Sql($"UPDATE organizations SET is_active=false WHERE id='{BillingDatabase.Org}'");
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.GetAsync("/api/organizations/me/analytics/operations")).StatusCode);
    }
    private sealed class MutationBetweenAggregates(BillingDatabase database) : DbCommandInterceptor
    {
        public bool Mutated { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(!Mutated && command.CommandText.Contains("GROUP BY") && command.CommandText.Contains("users"))
            {
                Mutated=true;
                await database.Sql($"UPDATE buildings SET is_active=false WHERE id='{BillingDatabase.Building}'; UPDATE organizations SET is_active=false WHERE id='{BillingDatabase.Org}'");
            }
            return result;
        }
    }
    [BillingPostgresFact]
    public async Task Repeatable_read_preserves_one_snapshot_during_concurrent_mutation()
    {
        await using var database=await BillingDatabase.Create(migrationHistory:true);var interceptor=new MutationBetweenAggregates(database);
        var services=new ServiceCollection();services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:DefaultConnection"]=database.Connection}).Build());
        using var provider=services.BuildServiceProvider();var options=new DbContextOptionsBuilder<Fire3DDbContext>(provider.GetRequiredService<DbContextOptions<Fire3DDbContext>>()).AddInterceptors(interceptor).Options;
        await using var context=new Fire3DDbContext(options);var query=new OperationsQueries(context);
        var result=await query.Platform(BillingDatabase.Admin,BillingDatabase.Admin,null,null,default);
        Assert.True(result.IsSuccess,result.Error?.Code);Assert.True(interceptor.Mutated);
        Assert.Equal(1,result.Value!.Buildings.Single(x=>x.IsActive).Count);
        Assert.Equal(2,result.Value.Organizations.Single(x=>x.IsActive).Count);
        Assert.Equal(false,await database.Scalar($"SELECT is_active FROM buildings WHERE id='{BillingDatabase.Building}'"));
        Assert.Null(context.Database.CurrentTransaction);
    }
}
