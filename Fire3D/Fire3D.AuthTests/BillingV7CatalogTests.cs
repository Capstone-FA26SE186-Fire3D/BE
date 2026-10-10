using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingV7CatalogTests
{
    [BillingPostgresFact]
    public async Task Actual_migration_history_creates_policy_contract_and_limited_grants()
    {
        await using var database=await BillingDatabase.Create(migrationHistory:true);
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261008090000_AddBillingV7Catalog'"));
        Assert.Equal(false,await database.Scalar("SELECT has_table_privilege('fire3d_api','billing_quota_policy_versions','DELETE')"));
        await Assert.ThrowsAsync<PostgresException>(()=>database.Sql($"INSERT INTO service_packages(id,code,name,unit_price,duration_months,commercial_version,learner_limit,ai_quota_units,created_by) VALUES(gen_random_uuid(),'BAD','Bad',100,1,7,10,0,'{BillingDatabase.Admin}')"));
    }
    [BillingPostgresFact]
    public async Task Catalog_requires_complete_v7_configuration_and_keeps_legacy_readable()
    {
        await using var database=await BillingDatabase.Create();
        await database.Sql($"INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at) VALUES(gen_random_uuid(),'LEGACY','Legacy',100,'VND',1,'{{}}',true,'{BillingDatabase.Admin}',now(),now())");
        using var factory=new BillingApiTests.Factory(database);
        using var admin=factory.As(BillingDatabase.Admin);using var owner=factory.As(BillingDatabase.Owner);
        var legacy=JsonDocument.Parse(await owner.GetStringAsync("/api/billing/service-packages")).RootElement[0];
        Assert.Equal(1,legacy.GetProperty("commercialVersion").GetInt32());
        Assert.False(legacy.GetProperty("isPurchasable").GetBoolean());
        foreach(var duration in new[]{1,5,7})
            Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync("/api/admin/service-packages",new {code="BAD",name="Bad",unitPrice=100,durationMonths=duration,learnerLimit=10,aiQuotaUnits=0})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync("/api/admin/service-packages",new {code="BAD",name="Bad",unitPrice=100,durationMonths=6,learnerLimit=10,aiQuotaUnits=1})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.PostAsJsonAsync("/api/admin/billing/quota-policies",new{})).StatusCode);
        foreach(var duration in new[]{6,12})
        {
            var response=await admin.PostAsJsonAsync("/api/admin/service-packages",new {code="V7_"+duration,name="V7",unitPrice=100,durationMonths=duration,learnerLimit=10,aiQuotaUnits=0});
            Assert.Equal(HttpStatusCode.Created,response.StatusCode);
            var value=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("Monthly",value.GetProperty("pricingBasis").GetString());Assert.True(value.GetProperty("isPurchasable").GetBoolean());
            Assert.Equal(100,value.GetProperty("unitPrice").GetDecimal());
        }
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM service_packages WHERE code='LEGACY' AND commercial_version=1 AND learner_limit IS NULL"));
    }

    [BillingPostgresFact]
    public async Task Policy_is_immutable_and_catalog_changes_obey_etag_and_audit_rollback()
    {
        await using var database=await BillingDatabase.Create();await using var db=database.Context();var service=new BillingService(db);
        var policy=await service.CreateQuotaPolicy(BillingDatabase.Admin,BillingDatabase.Admin,new("tokens",DateTimeOffset.UtcNow.AddDays(-1)),default);
        var request=new PackageWriteRequest("V7","V7",100,6,true,null,10,50,policy.Id);
        var package=await service.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,request,null,default);
        Assert.True(package.IsPurchasable);
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>database.Sql($"UPDATE billing_quota_policy_versions SET quota_unit='other' WHERE id='{policy.Id}'"));
        Assert.Equal("23514",denied.SqlState);
        Assert.Equal(false,await database.Scalar("SELECT has_table_privilege('fire3d_api','billing_quota_policy_versions','UPDATE')"));
        var stale=await Assert.ThrowsAsync<BillingException>(()=>service.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,package.Id,request,BillingETag.Format(package.Id,99),default));
        Assert.Equal(412,stale.Status);
        await database.Sql("ALTER TABLE audit_logs ADD CONSTRAINT reject_package_audit CHECK(target_entity<>'service_packages') NOT VALID");
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(()=>service.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,package.Id,request with {LearnerLimit=20},BillingETag.Format(package.Id,package.Revision),default));
        Assert.Equal(10,await database.Scalar($"SELECT learner_limit FROM service_packages WHERE id='{package.Id}'"));
        Assert.Equal(1L,await database.Scalar($"SELECT revision FROM service_packages WHERE id='{package.Id}'"));
    }
}
