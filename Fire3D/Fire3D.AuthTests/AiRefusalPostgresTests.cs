using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Organization_ai_keeps_what_the_provider_said_when_it_refuses_the_pinned_input()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant();
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            var source = await ApprovedSource(gates, admin);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("Answer", "tokens", 100, 1000, 5), "refusal-policy", null, default)).IsSuccess);
            await SeedAiGrant(tenant.Org, 500);

            // The service names the document it could not use: Admin sees it, the failure code stays the stable one.
            var named = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Hash?"), "refusal-named", default)).Value.GetProperty("requestId").GetGuid();
            var fake = new FakeFastApi
            {
                Respond = (_, _) => new((HttpStatusCode)422)
                {
                    Content = JsonContent.Create(new { code = "AI_SOURCE_HASH_MISMATCH", message = "Source content hash does not match the pinned hash", document_id = source })
                }
            };
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            var view = (await gates.AdminAsync("GetRequest", adminId, admin, named, new { }, null, null, default)).Value;
            Assert.Equal("Failed", view.GetProperty("status").GetString());
            Assert.Equal("AI_SOURCE_HASH_MISMATCH", view.GetProperty("failureCode").GetString());
            var refusal = view.GetProperty("providerResult").GetProperty("refusal");
            Assert.Equal("422", refusal.GetProperty("httpStatus").GetString());
            Assert.Equal("AI_SOURCE_HASH_MISMATCH", refusal.GetProperty("code").GetString());
            Assert.Equal("Source content hash does not match the pinned hash", refusal.GetProperty("message").GetString());
            Assert.Equal(source.ToString(), refusal.GetProperty("documentId").GetString());
            Assert.Equal(100L, await AllocationUnits(named, "Released"));

            // The organization never sees provider_result.
            var org = (await gates.OrganizationAsync("Get", tenant.User, tenant.Family, named, new { }, null, default)).Value;
            Assert.False(org.TryGetProperty("providerResult", out _));
            Assert.Equal("AI_SOURCE_HASH_MISMATCH", org.GetProperty("failureCode").GetString());

            // A refusal without a readable body is still recorded with its status; the failure code is the generic one.
            var bare = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Bare?"), "refusal-bare", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, _) => new(HttpStatusCode.BadRequest) { Content = new StringContent("not json") };
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            var bareView = (await gates.AdminAsync("GetRequest", adminId, admin, bare, new { }, null, null, default)).Value;
            Assert.Equal("AI_PROVIDER_REJECTED", bareView.GetProperty("failureCode").GetString());
            var bareRefusal = bareView.GetProperty("providerResult").GetProperty("refusal");
            Assert.Equal("400", bareRefusal.GetProperty("httpStatus").GetString());
            Assert.False(bareRefusal.TryGetProperty("message", out _));
            Assert.False(bareRefusal.TryGetProperty("documentId", out _));

            // A request that did not fail has no refusal.
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM ai_requests WHERE id NOT IN ('{named}','{bare}') AND provider_result ? 'refusal'"));
        });
    }
}
