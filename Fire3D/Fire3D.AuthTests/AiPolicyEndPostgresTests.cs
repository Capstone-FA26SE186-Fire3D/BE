using System.Text.Json;
using Fire3D.Application.Ai;
using Fire3D.Application.Authentication;
using Fire3D.Infrastructure.Ai;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // #176: only units the AI service settles, and ending a policy is the one change allowed after creation.
    [PostgresFact]
    public async Task Ai_policies_accept_only_service_units_and_can_only_be_ended()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant();
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            Task<AuthResult<JsonElement>> End(Guid id, object input, string? key) => gates.AdminAsync("EndPolicy", adminId, admin, id, input, key, null, default);
            CreateAiPolicyRequest Trainee(string unit, DateTimeOffset? from = null) => new("TraineeAnswer", unit, 1, 1000, 5, from, DailyUnits: 20);

            Assert.Equal("AI_QUOTA_UNIT_UNSUPPORTED", (await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, Trainee("luot-hoi"), "unit", null, default)).Error?.Code);
            var open = await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, Trainee("requests"), "open", null, default);
            Assert.True(open.IsSuccess, open.Error?.Code);
            var id = open.Value.GetProperty("id").GetGuid();
            var ends = DateTimeOffset.UtcNow.AddHours(1);
            Assert.Equal("AI_POLICY_OVERLAP", (await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, Trainee("requests", ends), "next", null, default)).Error?.Code);

            Assert.Equal("FORBIDDEN", (await gates.AdminAsync("EndPolicy", tenant.User, tenant.Family, id, new { }, "tenant", null, default)).Error?.Code);
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await End(id, new { }, null)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await End(Guid.NewGuid(), new { }, "missing")).Error?.Code);
            Assert.Equal("AI_POLICY_END_INVALID", (await End(id, new { effectiveUntil = DateTimeOffset.UtcNow.AddHours(-1) }, "past")).Error?.Code);

            var ended = await End(id, new { effectiveUntil = ends }, "end");
            Assert.True(ended.IsSuccess, ended.Error?.Code);
            Assert.Equal(ends.UtcDateTime, ended.Value.GetProperty("effectiveUntil").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
            Assert.Equal("requests", ended.Value.GetProperty("quotaUnit").GetString());
            Assert.Equal(ended.Value.GetRawText(), (await End(id, new { effectiveUntil = ends }, "end")).Value.GetRawText());
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", (await End(id, new { }, "end")).Error?.Code);
            Assert.Equal("AI_POLICY_END_INVALID", (await End(id, new { effectiveUntil = ends.AddHours(1) }, "extend")).Error?.Code);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, Trainee("requests", ends), "next", null, default)).IsSuccess);

            Assert.True((await End(id, new { }, "now")).IsSuccess);
            Assert.Equal("AI_POLICY_ENDED", (await End(id, new { }, "again")).Error?.Code);
            // Two ends were applied; the replay wrote nothing.
            Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='AiOperationPolicy' AND target_id='{id}' AND new_values->>'operation'='EndPolicy' AND new_values ? 'effectiveUntil' AND user_id='{adminId}'"));
        });

        // HTTP: the unit issue carries its own code; /end works without a body (ends now) and needs an Idempotency-Key.
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var unit = await AiSend(client, AiRequest(HttpMethod.Post, "/api/admin/ai/policies", new { operation = "Answer", quotaUnit = "credit", reserveUnits = 1, maxInputChars = 1000, maxSources = 5 }, "http-unit"));
        Assert.Equal((System.Net.HttpStatusCode)422, unit.Status);
        Assert.Contains(unit.Body.GetProperty("issues").EnumerateArray(), x => x.GetProperty("path").GetString() == "$.quotaUnit" && x.GetProperty("code").GetString() == "AI_QUOTA_UNIT_UNSUPPORTED");
        var answer = await AiSend(client, AiRequest(HttpMethod.Post, "/api/admin/ai/policies", new { operation = "Answer", quotaUnit = "requests", reserveUnits = 1, maxInputChars = 1000, maxSources = 5 }, "http-answer"));
        var answerId = answer.Body.GetProperty("id").GetGuid();
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await AiSend(client, AiRequest(HttpMethod.Post, $"/api/admin/ai/policies/{answerId}/end", null))).Status);
        var endedNow = await AiSend(client, AiRequest(HttpMethod.Post, $"/api/admin/ai/policies/{answerId}/end", null, "http-end"));
        Assert.Equal(System.Net.HttpStatusCode.OK, endedNow.Status);
        Assert.NotEqual(JsonValueKind.Null, endedNow.Body.GetProperty("effectiveUntil").ValueKind);
        Assert.Equal("AI_POLICY_ENDED", (await AiSend(client, AiRequest(HttpMethod.Post, $"/api/admin/ai/policies/{answerId}/end", new { }, "http-end-again"))).Body.GetProperty("code").GetString());

        // Every other change, and deletes, stay forbidden even for the owner; the migration's own update (end now) is allowed.
        var legacy = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO ai_operation_policies(id,operation,quota_unit,reserve_units,max_input_chars,max_sources,effective_from,created_by,created_at) VALUES('{legacy}','ScenarioDraft','luot-hoi',1,1000,5,now()-interval '1 day','{adminId}',now())");
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE ai_operation_policies SET quota_unit='requests' WHERE id='{legacy}'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE ai_operation_policies SET effective_until=now()-interval '1 hour' WHERE id='{legacy}'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM ai_operation_policies WHERE id='{legacy}'"));
        await ExecuteAsync($"UPDATE ai_operation_policies SET effective_until=now() WHERE id='{legacy}'");
        Assert.Equal(true, await ScalarAsync($"SELECT effective_until IS NOT NULL FROM ai_operation_policies WHERE id='{legacy}'"));
    }
}
