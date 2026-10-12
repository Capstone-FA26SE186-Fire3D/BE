using System.Net;
using System.Text.Json;
using Fire3D.Infrastructure.Reporting;
using Fire3D.Infrastructure.Scenarios;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Admin_reads_training_analytics_of_one_organization()
    {
        var tenant = await SeedAiTenant();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var response = await client.GetAsync($"/api/admin/organizations/{tenant.Org}/analytics/training");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(tenant.Org, body.GetProperty("organization").GetProperty("id").GetGuid());
        Assert.False(body.TryGetProperty("byOrganization", out _));
        Assert.True(body.TryGetProperty("aiUsage", out _)); Assert.True(body.TryGetProperty("definitions", out _));
        await ExecuteAsync($"UPDATE organizations SET is_active=false WHERE id='{tenant.Org}'");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/admin/organizations/{tenant.Org}/analytics/training")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/organizations/{Guid.NewGuid()}/analytics/training")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/admin/organizations/{tenant.Org}/analytics/training?from=2026-01-01T00:00:00Z&to=2026-06-01T00:00:00Z")).StatusCode);
        var other = await SeedAiTenant();
        using var org = await RoleClient("OrganizationUser", other.Org);
        Assert.Equal(HttpStatusCode.Forbidden, (await org.GetAsync($"/api/admin/organizations/{other.Org}/analytics/training")).StatusCode);
    }

    [PostgresFact]
    public async Task Audit_detail_shows_the_content_rejection_reason_from_the_review()
    {
        var f = await SeedReadyPackage();
        await using var db = BuildingContext(testConnection);
        var readiness = new ScenarioReadinessStore(db);
        var submitted = await readiness.ExecuteAsync("Submit", f.Owner, f.Owner, f.Version, null, new { }, "submit", default);
        Assert.True((await readiness.ExecuteAsync("Reject", adminId, adminId, f.Version, null, new { contentHash = submitted.Value.GetProperty("contentHash").GetString(), rubricHash = submitted.Value.GetProperty("rubricHash").GetString(), reason = "Add the second stairwell." }, "reject", default)).IsSuccess);
        var rejection = (Guid)(await ScalarAsync($"SELECT id FROM audit_logs WHERE target_entity='ScenarioVersion' AND target_id='{f.Version}' AND action='Reject'"))!;
        var submission = (Guid)(await ScalarAsync($"SELECT id FROM audit_logs WHERE target_entity='ScenarioVersion' AND target_id='{f.Version}' AND action='Update' ORDER BY created_at LIMIT 1"))!;
        var audits = new AuditQueries(db);
        Assert.Equal("Add the second stairwell.", (await audits.Get(adminId, adminId, rejection, default)).Value!.Reason);
        Assert.Null((await audits.Get(adminId, adminId, submission, default)).Value!.Reason);
    }
}
