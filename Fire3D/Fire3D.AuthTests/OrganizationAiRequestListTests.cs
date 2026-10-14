using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Organization_lists_only_its_own_ai_requests_with_filters()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant(); var other = await SeedAiTenant();
        var login = "ai_list_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION ai_org_gate(text,uuid,uuid,uuid,jsonb,text),ai_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint),ai_org_request_list_gate(uuid,uuid,jsonb) TO {login}");
        try
        {
            await using var db = BuildingContext(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString);
            var gates = new OrganizationAiGates(db);
            var source = await ApprovedSource(gates, admin);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("Answer", "tokens", 100, 1000, 5), "policy", null, default)).IsSuccess);
            await SeedAiGrant(tenant.Org, 1000); await SeedAiGrant(other.Org, 1000);
            var mine = await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source), "list-1", default);
            Assert.True(mine.IsSuccess, mine.Error?.Code);
            Assert.True((await gates.OrganizationAsync("Submit", other.User, other.Family, Guid.Empty, AnswerInput(source), "list-2", default)).IsSuccess);

            var list = (await gates.OrganizationRequestsAsync(tenant.User, tenant.Family, new { page = 1, pageSize = 20 }, default)).Value;
            Assert.Equal(1, list.GetProperty("total").GetInt32());
            Assert.Equal(mine.Value.GetProperty("requestId").GetGuid(), list.GetProperty("items")[0].GetProperty("requestId").GetGuid());
            Assert.Equal(1, (await gates.OrganizationRequestsAsync(tenant.User, tenant.Family, new { status = "Accepted", operation = "Answer", mine = true }, default)).Value.GetProperty("total").GetInt32());
            Assert.Equal(0, (await gates.OrganizationRequestsAsync(tenant.User, tenant.Family, new { operation = "ScenarioDraft" }, default)).Value.GetProperty("total").GetInt32());
            Assert.Equal("VALIDATION_ERROR", (await gates.OrganizationRequestsAsync(tenant.User, tenant.Family, new { status = "Done" }, default)).Error?.Code);
            // The pre-v7 names are no longer accepted.
            Assert.Equal("VALIDATION_ERROR", (await gates.OrganizationRequestsAsync(tenant.User, tenant.Family, new { status = "Queued" }, default)).Error?.Code);
            Assert.Equal(0, (await gates.OrganizationRequestsAsync(tenant.User, tenant.Family, new { resultType = "KnowledgeAnswer" }, default)).Value.GetProperty("total").GetInt32());
            Assert.Equal("FORBIDDEN", (await gates.OrganizationRequestsAsync(adminId, admin, new { }, default)).Error?.Code);
            await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{tenant.Family}'");
            Assert.Equal("UNAUTHORIZED", (await gates.OrganizationRequestsAsync(tenant.User, tenant.Family, new { }, default)).Error?.Code);
        }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }
}
