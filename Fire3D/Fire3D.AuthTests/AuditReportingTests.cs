using System.Net;
using System.Text.Json;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Reporting;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class AuditReportingTests
{
    [Fact]
    public void Audit_projection_excludes_secrets_unknown_actions_and_unsafe_values()
    {
        var audit=new AuditLog{TargetEntity="Release",Action=AuditAction.Publish,OldValues="{\"status\":\"Built\"}",NewValues="{\"status\":\"Published\",\"password\":\"secret\",\"email\":\"private@example.test\",\"signedUrl\":\"secret\"}"};
        var change=Assert.Single(SafeAuditChanges.Read(audit));Assert.Equal("status",change.Field);Assert.Equal("Built",change.Before);Assert.Equal("Published",change.After);
        audit.NewValues="{\"status\":\"secret\",\"publishedAt\":{\"token\":\"secret\"}}";audit.OldValues=null;Assert.Empty(SafeAuditChanges.Read(audit));
        audit.TargetEntity="legacy";audit.NewValues="{\"revision\":5}";Assert.Empty(SafeAuditChanges.Read(audit));
        audit.TargetEntity="Release";audit.NewValues="broken";Assert.Empty(SafeAuditChanges.Read(audit));
    }
    [BillingPostgresFact]
    public async Task Audit_HTTP_filters_paging_timezone_and_allowlist_are_enforced()
    {
        await using var db=await BillingDatabase.Create(migrationHistory:true);using var factory=new BillingApiTests.Factory(db);
        using var admin=factory.As(BillingDatabase.Admin);using var owner=factory.As(BillingDatabase.Owner);
        var ids=Enumerable.Range(1,3).Select(x=>Guid.Parse($"40000000-0000-0000-0000-{x:D12}")).ToArray();
        foreach(var id in ids)await db.Sql($$"""
            INSERT INTO audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
            VALUES('{{id}}','{{BillingDatabase.Admin}}','{{BillingDatabase.Org}}','User','Publish','Release','{{id}}','{{id}}','{"status":"Published","token":"secret","email":"private@example.test"}',now());
            """);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.GetAsync("/api/admin/audit-logs")).StatusCode);
        foreach(var query in new[]{"action=999","action=3","from=2026-01-01T00:00:00","pageSize=101","from=2026-01-01T00:00:00Z&to=2026-07-01T00:00:00Z"})
            Assert.Equal(HttpStatusCode.BadRequest,(await admin.GetAsync("/api/admin/audit-logs?"+query)).StatusCode);
        using var page=JsonDocument.Parse(await (await admin.GetAsync("/api/admin/audit-logs?targetEntity=Release&pageSize=1")).Content.ReadAsStringAsync());
        Assert.Equal(3,page.RootElement.GetProperty("total").GetInt32());Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        var response=await admin.GetAsync($"/api/admin/audit-logs/{ids[0]}");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var text=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("secret",text);Assert.DoesNotContain("private@example.test",text);Assert.Contains("changes",text);Assert.Contains("Published",text);
        await db.Sql($"UPDATE users SET is_active=false WHERE id='{BillingDatabase.Admin}'");Assert.Equal(HttpStatusCode.Forbidden,(await admin.GetAsync("/api/admin/audit-logs")).StatusCode);
    }
}
