using System.Text.Json;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#117: AI usage analytics count requests by the v7 statuses, split Succeeded by result type, and separate audiences platform-wide.
    [PostgresFact]
    public async Task Ai_usage_analytics_count_v7_statuses_result_types_and_audiences()
    {
        var org = Guid.NewGuid(); var other = Guid.NewGuid();
        string Row(Guid? organization, string audience, string operation, string status, string? resultType) =>
            $"(gen_random_uuid(),{(organization is null ? "NULL" : $"'{organization}'")},gen_random_uuid(),'{audience}','{operation}',gen_random_uuid(),'tokens',5,'{status}'," +
            $"{(status == "Succeeded" ? "1" : "NULL")},'{{}}'::jsonb,repeat('a',64),'[]'::jsonb,{(status == "Succeeded" ? "'{}'::jsonb" : "NULL")},{(resultType is null ? "NULL" : $"'{resultType}'")}," +
            $"{(status == "Processing" ? "gen_random_uuid(),now()+interval '1 minute'" : "NULL,NULL")},{(status is "Succeeded" or "Failed" or "Rejected" ? "now()" : "NULL")},now(),'2026-03-05T00:00:00Z',now())";
        var rows = string.Join(",", new[]
        {
            Row(org, "organization", "Answer", "Accepted", null),
            Row(org, "organization", "Answer", "Processing", null),
            Row(org, "organization", "Answer", "Succeeded", "KnowledgeAnswer"),
            Row(org, "organization", "ScenarioDraft", "Succeeded", "ScenarioDraft"),
            Row(org, "organization", "Answer", "Succeeded", "RejectedBySafetyGate"),
            Row(org, "organization", "Answer", "NeedsReconcile", null),
            Row(other, "organization", "Answer", "Failed", null),
            Row(null, "trainee", "TraineeAnswer", "Succeeded", "InsufficientEvidence"),
            Row(org, "organization", "Answer", "Rejected", null),
            // Outside [from,to): never counted.
            Row(org, "organization", "Answer", "Accepted", null).Replace("'2026-03-05T00:00:00Z'", "'2026-04-02T00:00:00Z'"),
            Row(org, "organization", "Answer", "Failed", null).Replace("'2026-03-05T00:00:00Z'", "'2026-02-28T23:59:59Z'")
        });
        // Policies, users and organizations are not needed to count rows: skip foreign keys for the fixture, keep every CHECK.
        await ExecuteAsync($"""
            SET session_replication_role=replica;
            INSERT INTO ai_requests(id,organization_id,actor_user_id,audience,operation,policy_id,quota_unit,reserved_units,status,consumed_units,input,input_hash,sources,result,result_type,lease_token,lease_until,completed_at,next_attempt_at,created_at,updated_at) VALUES {rows};
            SET session_replication_role=origin;
            """);
        async Task<JsonElement> Usage(string scope) => JsonDocument.Parse((string)(await ScalarAsync($"SELECT analytics_ai_usage({scope},'2026-03-01','2026-04-01',now())::text"))!).RootElement.Clone();

        var mine = await Usage($"'{org}'");
        // jsonb objects have no key order: compare by key.
        static Dictionary<string, int> Counts(JsonElement e) => e.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32());
        Assert.Equal(new Dictionary<string, int> { ["Accepted"] = 1, ["Processing"] = 1, ["Succeeded"] = 3, ["Failed"] = 0, ["NeedsReconcile"] = 1, ["Rejected"] = 1 }, Counts(mine.GetProperty("requests")));
        Assert.Equal(new Dictionary<string, int> { ["KnowledgeAnswer"] = 1, ["ScenarioDraft"] = 1, ["InsufficientEvidence"] = 0, ["RejectedBySafetyGate"] = 1 }, Counts(mine.GetProperty("resultTypes")));
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("byAudience").ValueKind);

        var platform = await Usage("NULL");
        Assert.Equal(4, platform.GetProperty("requests").GetProperty("Succeeded").GetInt32());
        Assert.Equal(1, platform.GetProperty("requests").GetProperty("Failed").GetInt32());
        Assert.Equal(1, platform.GetProperty("resultTypes").GetProperty("InsufficientEvidence").GetInt32());
        Assert.Equal(3, platform.GetProperty("byAudience").GetProperty("organization").GetProperty("Succeeded").GetInt32());
        Assert.Equal(1, platform.GetProperty("byAudience").GetProperty("trainee").GetProperty("Succeeded").GetInt32());
        Assert.False(platform.GetProperty("requests").TryGetProperty("Queued", out _));
    }
}
