using System.Net;
using System.Text.Json;
using Fire3D.Application.Reporting;
using Fire3D.Infrastructure.Reporting;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task WithAnalyticsRuntime(Func<string, Task> action)
    {
        var login = "analytics_runtime_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION analytics_gate(text,uuid,uuid,timestamp with time zone,timestamp with time zone,integer) TO {login}");
        try { await action(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString); }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }

    // Shape-only rows: provenance of sessions/payments is covered by the learner and PayOS suites.
    private Task SeedAnalyticsSession(Guid org, Guid trainee, Guid training, string startedAt, string status = "Completed", string mode = "Assessment",
        string? outcome = null, string? resultAt = null, string? launchedAt = null, string? endedAt = null, string? heartbeatAt = null)
    {
        var id = Guid.NewGuid();
        var result = outcome is null ? "" : $"""
            INSERT INTO session_results(id,session_id,completion_key,last_event_sequence,submission_payload,result_schema_version,rubric_version,synced_at,created_at,contract_version,mode,outcome,outcome_reason,metrics)
            VALUES(gen_random_uuid(),'{id}',gen_random_uuid(),1,jsonb_build_object(),'7','1',{resultAt},{resultAt},7,'{mode}','{outcome}','FIXTURE',jsonb_build_object());
            """;
        return ExecuteAsync($"""
            SET session_replication_role=replica;
            INSERT INTO sessions(id,training_id,release_id,scenario_version_id,organization_id,trainee_user_id,start_key,protocol_version,scenario_hash,release_hash,started_at,launched_at,ended_at,last_heartbeat_at,
             contract_version,building_id,entitlement_id,seat_id,mode,status,package,runtime_version,launch_family,grant_generation,grant_issued_at,grant_expires_at)
            VALUES('{id}','{training}',gen_random_uuid(),gen_random_uuid(),'{org}','{trainee}',gen_random_uuid(),'1.0','{new string('a', 64)}','{new string('b', 64)}',{startedAt},{launchedAt ?? "NULL"},{endedAt ?? "NULL"},{heartbeatAt ?? "NULL"},
             7,gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'{mode}','{status}',jsonb_build_object(),'1.0.0',gen_random_uuid(),1,({startedAt})::timestamptz,({startedAt})::timestamptz+interval '5 minutes');
            {result}
            SET session_replication_role=origin;
            """);
    }

    private Task SeedAppliedPayment(Guid org, decimal amount, string receivedAt, string status = "Applied") => ExecuteAsync($"""
        SET session_replication_role=replica;
        WITH r AS (INSERT INTO payos_payment_requests(id,idempotency_key,status,quotation_id,organization_id,requested_by,order_code,expected_amount,expected_currency,checkout_url,return_url,cancel_url)
          VALUES(gen_random_uuid(),'fixture-'||gen_random_uuid(),'Pending',gen_random_uuid(),'{org}',gen_random_uuid(),(random()*1e12)::bigint,{amount},'VND','https://pay.test','https://fet3d.test/ok','https://fet3d.test/cancel') RETURNING id,order_code)
        INSERT INTO payment_transactions(id,status,payment_request_id,webhook_event_id,received_order_code,received_amount,received_currency,signature_verified,raw_payload,received_at,signature_verified_at,processed_at,rejection_reason)
        SELECT gen_random_uuid(),'{status}',r.id,'evt-'||gen_random_uuid(),r.order_code,{amount},'VND',true,jsonb_build_object(),{receivedAt},{receivedAt},{receivedAt},{(status == "Rejected" ? "'fixture rejection'" : "NULL")} FROM r;
        SET session_replication_role=origin;
        """);

    private static LearnerAnalytics Analytics(string runtime) => new(BuildingContext(runtime), Options.Create(new LearnerAnalyticsOptions()));

    [PostgresFact]
    public async Task Training_analytics_use_start_cohort_server_results_heartbeats_and_tenant_scope()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant(); var other = await SeedAiTenant();
        var from = "2026-01-01T00:00:00Z"; var to = "2026-01-31T00:00:00Z";
        await WithAnalyticsRuntime(async runtime =>
        {
            // Empty cohort: counts are 0 and rates without samples are null.
            var empty = (await Analytics(runtime).Read("OrganizationTraining", tenant.User, tenant.Family, from, to, default)).Value;
            Assert.Equal(0, empty.GetProperty("plays").GetInt32());
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("completionRate").ValueKind);
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("assessment").GetProperty("passRate").ValueKind);
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("duration").GetProperty("averageSeconds").ValueKind);
            Assert.Equal(0, empty.GetProperty("outcomes").GetProperty("NotAssessed").GetInt32());
            Assert.False(empty.TryGetProperty("byOrganization", out _));
            Assert.Equal(0, empty.GetProperty("aiUsage").GetProperty("needsReconcile").GetProperty("requests").GetInt32());

            var trainee = Guid.NewGuid(); var second = Guid.NewGuid(); var training = Guid.NewGuid();
            // Included: start exactly at from, passed, 300 s between launch and end.
            await SeedAnalyticsSession(tenant.Org, trainee, training, "'2026-01-01T00:00:00Z'", outcome: "Passed", resultAt: "'2026-01-01T00:06:00Z'", launchedAt: "'2026-01-01T00:01:00Z'", endedAt: "'2026-01-01T00:06:00Z'");
            // Included: started in range, result synced offline after the range still belongs to this cohort.
            await SeedAnalyticsSession(tenant.Org, trainee, training, "'2026-01-30T23:00:00Z'", outcome: "NotPassed", resultAt: "'2026-02-05T00:00:00Z'", launchedAt: "'2026-01-30T23:01:00Z'", endedAt: "'2026-01-30T23:11:00Z'");
            // Included: still running with a fresh heartbeat (active now), no result.
            await SeedAnalyticsSession(tenant.Org, second, training, "'2026-01-15T00:00:00Z'", status: "Running", mode: "Learn", heartbeatAt: "now()-interval '30 seconds'");
            // Running but stale heartbeat: not active.
            await SeedAnalyticsSession(tenant.Org, second, training, "'2026-01-16T00:00:00Z'", status: "Running", mode: "Learn", heartbeatAt: "now()-interval '10 minutes'");
            // Excluded: start exactly at to, start before from (its result lands in range), and another tenant.
            await SeedAnalyticsSession(tenant.Org, trainee, training, "'2026-01-31T00:00:00Z'", outcome: "Passed", resultAt: "'2026-01-31T00:10:00Z'");
            await SeedAnalyticsSession(tenant.Org, trainee, training, "'2025-12-31T23:59:59Z'", outcome: "Passed", resultAt: "'2026-01-02T00:00:00Z'");
            await SeedAnalyticsSession(other.Org, Guid.NewGuid(), Guid.NewGuid(), "'2026-01-10T00:00:00Z'", mode: "Guided", outcome: "Incomplete", resultAt: "'2026-01-10T00:20:00Z'");

            var org = (await Analytics(runtime).Read("OrganizationTraining", tenant.User, tenant.Family, from, to, default)).Value;
            Assert.Equal(4, org.GetProperty("plays").GetInt32());
            Assert.Equal(2, org.GetProperty("uniqueTrainees").GetInt32());
            Assert.Equal(2, org.GetProperty("completed").GetInt32());
            Assert.Equal(0.5m, org.GetProperty("completionRate").GetDecimal());
            Assert.Equal(1, org.GetProperty("activeSessions").GetInt32());
            Assert.Equal(1, org.GetProperty("outcomes").GetProperty("Passed").GetInt32());
            Assert.Equal(1, org.GetProperty("outcomes").GetProperty("NotPassed").GetInt32());
            Assert.Equal(0, org.GetProperty("outcomes").GetProperty("Incomplete").GetInt32());
            Assert.Equal(0.5m, org.GetProperty("assessment").GetProperty("passRate").GetDecimal());
            Assert.Equal(2, org.GetProperty("duration").GetProperty("sessions").GetInt32());
            Assert.Equal(450m, org.GetProperty("duration").GetProperty("averageSeconds").GetDecimal());
            Assert.Equal(2, org.GetProperty("byMode").EnumerateArray().Single(x => x.GetProperty("mode").GetString() == "Learn").GetProperty("plays").GetInt32());
            var byTraining = Assert.Single(org.GetProperty("byTraining").EnumerateArray());
            Assert.Equal(4, byTraining.GetProperty("plays").GetInt32());
            Assert.Equal(120, org.GetProperty("activeWindowSeconds").GetInt32());
            Assert.True(org.GetProperty("definitions").TryGetProperty("completion", out _));

            var platform = (await Analytics(runtime).Read("PlatformTraining", adminId, admin, from, to, default)).Value;
            Assert.Equal(5, platform.GetProperty("plays").GetInt32());
            Assert.Equal(1, platform.GetProperty("outcomes").GetProperty("Incomplete").GetInt32());
            Assert.Equal(2, platform.GetProperty("byOrganization").GetArrayLength());

            Assert.Equal("FORBIDDEN", (await Analytics(runtime).Read("PlatformTraining", tenant.User, tenant.Family, from, to, default)).Error?.Code);
            Assert.Equal("FORBIDDEN", (await Analytics(runtime).Read("OrganizationTraining", adminId, admin, from, to, default)).Error?.Code);
            Assert.Equal("VALIDATION_ERROR", (await Analytics(runtime).Read("OrganizationTraining", tenant.User, tenant.Family, "2026-01-01T00:00:00Z", "2026-04-02T00:00:00Z", default)).Error?.Code);
            Assert.Equal("VALIDATION_ERROR", (await Analytics(runtime).Read("OrganizationTraining", tenant.User, tenant.Family, "2026-01-01", to, default)).Error?.Code);
            await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{tenant.Family}'");
            Assert.Equal("UNAUTHORIZED", (await Analytics(runtime).Read("OrganizationTraining", tenant.User, tenant.Family, from, to, default)).Error?.Code);
        });
    }

    [PostgresFact]
    public async Task Revenue_and_ai_usage_analytics_count_received_money_and_settled_units_only()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant(); var other = await SeedAiTenant();
        var from = "2026-03-01T00:00:00Z"; var to = "2026-03-31T00:00:00Z";
        await SeedAppliedPayment(tenant.Org, 500000, "'2026-03-10T10:00:00Z'");
        await SeedAppliedPayment(other.Org, 250000, "'2026-03-10T23:00:00Z'");
        await SeedAppliedPayment(tenant.Org, 900000, "'2026-03-12T00:00:00Z'", "Rejected");
        await SeedAppliedPayment(tenant.Org, 700000, "'2026-03-31T00:00:00Z'");
        await SeedAiGrant(tenant.Org, 1000);
        await ExecuteAsync($"""
            INSERT INTO billing_ai_quota_allocations(grant_id,organization_id,request_id,quota_unit,reserved_units,consumed_units,status,created_at,settled_at)
             SELECT id,organization_id,gen_random_uuid(),'tokens',100,40,'Settled','2026-03-05T00:00:00Z','2026-03-05T00:01:00Z' FROM billing_ai_quota_grants WHERE organization_id='{tenant.Org}';
            INSERT INTO billing_ai_quota_allocations(grant_id,organization_id,request_id,quota_unit,reserved_units,consumed_units,status,created_at,settled_at)
             SELECT id,organization_id,gen_random_uuid(),'tokens',100,70,'Settled','2026-04-05T00:00:00Z','2026-04-05T00:01:00Z' FROM billing_ai_quota_grants WHERE organization_id='{tenant.Org}';
            INSERT INTO billing_ai_quota_allocations(grant_id,organization_id,request_id,quota_unit,reserved_units,status)
             SELECT id,organization_id,gen_random_uuid(),'tokens',100,'Reserved' FROM billing_ai_quota_grants WHERE organization_id='{tenant.Org}';
            """);
        await WithAnalyticsRuntime(async runtime =>
        {
            var revenue = (await Analytics(runtime).Read("PlatformRevenue", adminId, admin, from, to, default)).Value;
            var total = Assert.Single(revenue.GetProperty("totals").EnumerateArray());
            Assert.Equal("VND", total.GetProperty("currency").GetString());
            Assert.Equal(750000m, total.GetProperty("amount").GetDecimal());
            Assert.Equal(2, total.GetProperty("transactions").GetInt32());
            Assert.Equal(2, revenue.GetProperty("payingOrganizations").GetInt32());
            Assert.Equal(["2026-03-10"], revenue.GetProperty("byDay").EnumerateArray().Select(x => x.GetProperty("date").GetString()).ToArray());
            Assert.Equal("Unknown", Assert.Single(revenue.GetProperty("byPurpose").EnumerateArray()).GetProperty("purpose").GetString());
            Assert.Equal("FORBIDDEN", (await Analytics(runtime).Read("PlatformRevenue", tenant.User, tenant.Family, from, to, default)).Error?.Code);

            var ai = (await Analytics(runtime).Read("OrganizationTraining", tenant.User, tenant.Family, from, to, default)).Value.GetProperty("aiUsage");
            Assert.Equal(40, Assert.Single(ai.GetProperty("settledUnits").EnumerateArray()).GetProperty("units").GetInt32());
            Assert.Equal(100, Assert.Single(ai.GetProperty("openReservations").EnumerateArray()).GetProperty("units").GetInt32());
            var otherAi = (await Analytics(runtime).Read("OrganizationTraining", other.User, other.Family, from, to, default)).Value.GetProperty("aiUsage");
            Assert.Equal(0, otherAi.GetProperty("settledUnits").GetArrayLength());
        });
    }

    [PostgresFact]
    public async Task Training_and_revenue_analytics_HTTP_enforce_roles_and_range()
    {
        var adminTokens = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminTokens.AccessToken);
        var training = await client.GetAsync("/api/admin/analytics/training");
        Assert.Equal(HttpStatusCode.OK, training.StatusCode);
        Assert.Contains("no-store", training.Headers.CacheControl?.ToString() ?? "");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/analytics/revenue?from=2026-01-01T00:00:00Z&to=2026-02-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/analytics/revenue?from=2026-01-01T00:00:00Z&to=2026-06-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/organizations/me/analytics/training")).StatusCode);
        using var anonymous = factory!.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/analytics/revenue")).StatusCode);
    }
}
