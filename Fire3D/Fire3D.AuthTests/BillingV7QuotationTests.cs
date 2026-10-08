using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
namespace Fire3D.AuthTests;

public sealed class BillingV7QuotationTests
{
    [BillingPostgresFact]
    public async Task Issue_pins_monthly_price_capacity_quota_and_calendar_period_then_catalog_changes_cannot_rewrite_it()
    {
        await using var database=await BillingDatabase.Create();await using var db=database.Context();var service=new BillingService(db);
        var policy=await service.CreateQuotaPolicy(BillingDatabase.Admin,new("tokens",DateTimeOffset.UtcNow.AddDays(-1)),default);
        var package=await service.SavePackage(BillingDatabase.Admin,null,new("V7","V7",100,6,true,null,25,1000,policy.Id),null,default);
        var quote=await service.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,package.Id,"New")]),"first",default);
        Assert.Equal(600,quote.TotalAmount);
        var start=new DateTimeOffset(DateTime.UtcNow.Year+1,8,31,0,0,0,TimeSpan.Zero);
        quote=await service.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,new(0,"Terms",DateTimeOffset.UtcNow.AddDays(1),[new(quote.Items[0].Id,start)]),BillingETag.Format(quote.Id,quote.Revision),default);
        Assert.Equal(start.UtcDateTime.AddMonths(6),quote.Items[0].EndsAt);
        Assert.Equal(25,quote.Items[0].LearnerLimit);Assert.Equal(1000,quote.Items[0].AiQuotaUnits);Assert.Equal("tokens",quote.Items[0].AiQuotaUnit);
        await service.SavePackage(BillingDatabase.Admin,package.Id,new("V7","V7",200,12,true,null,50,0),BillingETag.Format(package.Id,package.Revision),default);
        var stored=await service.GetQuotation(BillingDatabase.Owner,quote.Id,default);
        Assert.Equal(600,stored.TotalAmount);Assert.Equal(1,stored.Items[0].PackageRevision);
        await Assert.ThrowsAsync<PostgresException>(()=>database.Sql($"UPDATE quotation_building_items SET learner_limit=999 WHERE quotation_id='{quote.Id}'"));
        await service.AcceptQuotation(BillingDatabase.Owner,BillingDatabase.Owner,quote.Id,BillingETag.Format(quote.Id,quote.Revision),default);
    }
    [BillingPostgresFact]
    public async Task Invalid_policy_interval_and_revoked_family_roll_back_issue()
    {
        await using var database=await BillingDatabase.Create();await using var db=database.Context();var service=new BillingService(db);
        var policy=await service.CreateQuotaPolicy(BillingDatabase.Admin,new("tokens",DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(10)),default);
        var package=await service.SavePackage(BillingDatabase.Admin,null,new("V7","V7",100,6,true,null,25,1000,policy.Id),null,default);
        var quote=await service.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,package.Id,"New")]),"first",default);
        var input=new IssueQuotationRequest(0,"Terms",DateTimeOffset.UtcNow.AddDays(1),[new(quote.Items[0].Id,DateTimeOffset.UtcNow.AddDays(2))]);
        var failure=await Assert.ThrowsAsync<BillingException>(()=>service.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,input,BillingETag.Format(quote.Id,quote.Revision),default));
        Assert.Equal("BILLING_POLICY_INTERVAL_INVALID",failure.Code);
        Assert.Equal("Draft",await database.Scalar($"SELECT status::text FROM quotations WHERE id='{quote.Id}'"));
        db.ChangeTracker.Clear();await database.Sql($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{BillingDatabase.Admin}'");
        failure=await Assert.ThrowsAsync<BillingException>(()=>service.IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,input,BillingETag.Format(quote.Id,quote.Revision),default));
        Assert.Equal(401,failure.Status);Assert.Equal("SESSION_REVOKED",failure.Code);
        Assert.Equal(0L,await database.Scalar($"SELECT count(*) FROM audit_logs WHERE target_id='{quote.Id}' AND action='Update'"));
    }
    [BillingPostgresFact]
    public async Task Concurrent_issue_is_serialized_by_etag()
    {
        await using var database=await BillingDatabase.Create();QuotationResponse quote;
        await using(var db=database.Context())
        {
            var service=new BillingService(db);var p=await service.SavePackage(BillingDatabase.Admin,null,new("V7","V7",100,12,true,null,10,0),null,default);
            quote=await service.CreateQuotation(BillingDatabase.Owner,BillingDatabase.Owner,new([new(BillingDatabase.Building,p.Id,"New")]),"first",default);
        }
        var input=new IssueQuotationRequest(0,"Terms",DateTimeOffset.UtcNow.AddDays(1),[new(quote.Items[0].Id,DateTimeOffset.UtcNow.AddDays(2))]);
        async Task<int> Issue()
        {
            await using var db=database.Context();
            try{await new BillingService(db).IssueQuotation(BillingDatabase.Admin,BillingDatabase.Admin,quote.Id,input,BillingETag.Format(quote.Id,quote.Revision),default);return 200;}
            catch(BillingException e){return e.Status;}
        }
        Assert.Equal(new[]{200,412},(await Task.WhenAll(Issue(),Issue())).Order().ToArray());
        Assert.Equal(1L,await database.Scalar($"SELECT count(*) FROM audit_logs WHERE target_id='{quote.Id}' AND action='Update'"));
    }
}
