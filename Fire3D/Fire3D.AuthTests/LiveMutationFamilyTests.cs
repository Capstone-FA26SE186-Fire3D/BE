using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class LiveMutationFamilyTests
{
    [BillingPostgresFact]
    public async Task Catalog_request_waiting_for_auth_lock_cannot_write_after_logout()
    {
        await using var database = await BillingDatabase.Create();
        using var factory = new BillingApiTests.Factory(database);
        using var admin = factory.As(BillingDatabase.Admin);
        await admin.GetAsync("/api/billing/service-packages");
        await using var held = new NpgsqlConnection(database.Connection); await held.OpenAsync();
        await using var tx = await held.BeginTransactionAsync();
        await new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:auth:{BillingDatabase.Admin}',0))",held,tx).ExecuteNonQueryAsync();
        var pending = admin.PostAsJsonAsync("/api/admin/service-packages",new PackageWriteRequest("WAIT","Wait",100,6,true,null,10,0));
        var waiting = false;
        for (var i=0;i<200;i++)
        {
            waiting = (bool)(await database.Scalar("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')"))!;
            if (waiting) break;
            await Task.Delay(25);
        }
        Assert.True(waiting,"Mutation must reach the held actor lock before revocation.");
        await new NpgsqlCommand($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{BillingDatabase.Admin}'",held,tx).ExecuteNonQueryAsync();
        await tx.CommitAsync();
        var response = await pending;
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        Assert.Contains("SESSION_REVOKED",await response.Content.ReadAsStringAsync());
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM service_packages"));
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task All_catalog_and_enterprise_mutations_require_an_owned_live_family()
    {
        await using var database = await BillingDatabase.Create();
        await using var db = database.Context(); var service = new BillingService(db);
        var package = await service.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("BASE","Base",100,6,true,null,10,0),null,default);
        var discountRequest = new DiscountWriteRequest("RULE","Percent",10,1,DateTimeOffset.UtcNow.AddDays(-1));
        var discount = await service.SaveDiscount(BillingDatabase.Admin,BillingDatabase.Admin,null,discountRequest,null,default);
        await database.Sql("UPDATE auth_refresh_tokens SET revoked_at=now()");
        async Task Denied(Func<Task> work) => Assert.Equal(401,(await Assert.ThrowsAsync<BillingException>(work)).Status);
        await Denied(()=>service.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("NEW","New",100,6,true,null,10,0),null,default));
        await Denied(()=>service.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,package.Id,new("BASE","Base",200,6,true,null,20,0),BillingETag.Format(package.Id,package.Revision),default));
        await Denied(()=>service.CreateQuotaPolicy(BillingDatabase.Admin,BillingDatabase.Admin,new("tokens",DateTimeOffset.UtcNow),default));
        await Denied(()=>service.SaveDiscount(BillingDatabase.Admin,BillingDatabase.Admin,null,discountRequest with {Code="NEW"},null,default));
        await Denied(()=>service.SaveDiscount(BillingDatabase.Admin,BillingDatabase.Admin,discount.Id,discountRequest,BillingETag.Format(discount.Id,discount.Revision),default));
        await Denied(()=>service.CreateEnterpriseRequest(BillingDatabase.Owner,BillingDatabase.Owner,new(1,6,"Contact","contact@example.test",null,null),"enterprise",default));
        Assert.Equal(2L,await database.Scalar("SELECT count(*) FROM audit_logs"));
        Assert.Equal(1L,await database.Scalar($"SELECT revision FROM service_packages WHERE id='{package.Id}'"));
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM billing_command_receipts"));
    }
}
