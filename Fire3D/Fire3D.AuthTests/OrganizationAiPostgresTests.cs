using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private sealed record AiTenant(Guid Org, Guid User, Guid Family, Guid Building);

    /// <summary>In-process stand-in for the FastAPI adapter; records every call and answers through <see cref="Respond"/>.</summary>
    private sealed class FakeFastApi : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<(HttpMethod Method, string Path, JsonObject? Body, string? RequestId)> Calls = [];
        public Func<HttpRequestMessage, JsonObject?, HttpResponseMessage> Respond = (_, _) => new(HttpStatusCode.ServiceUnavailable);
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject;
            lock (Calls) Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body, request.Headers.TryGetValues("X-Request-Id", out var v) ? v.Single() : null));
            return Respond(request, body);
        }
        public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        public static object Answer(string id, Guid source, int units, string kind = "KnowledgeSource") =>
            new { requestId = id, outcome = "Succeeded", answer = "Use the nearest protected stair.", citations = new[] { new { kind, id = source, locator = "Article 3" } }, usage = new { quotaUnit = "tokens", units } };
    }

    private async Task WithAiRuntime(Func<string, Task> action)
    {
        var login = "ai_runtime_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}';GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION ai_org_gate(text,uuid,uuid,uuid,jsonb,text),ai_worker_gate(text,text,uuid,uuid,jsonb),ai_admin_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO {login}");
        try { await action(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString); }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }

    private async Task<AiTenant> SeedAiTenant()
    {
        var org = Guid.NewGuid(); var user = Guid.NewGuid(); var building = Guid.NewGuid(); var family = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO organizations(id,name,slug) VALUES('{org}','AI organization','ai-{org:N}');
            INSERT INTO users(id,organization_id,email,full_name,role,is_active,email_verified_at,created_at,updated_at) VALUES('{user}','{org}','ai-{user:N}@example.test','AI user','OrganizationUser',true,now(),now(),now());
            INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{building}','{org}','AI building',true,'{user}',now(),now());
            INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{user}','{family}','{Guid.NewGuid():N}{Guid.NewGuid():N}',now(),now()+interval '1 hour');
            """);
        return new(org, user, family, building);
    }

    // Grant provenance (payment + top-up line) is covered by the billing suite; here only the ledger row matters.
    private Task SeedAiGrant(Guid org, int units, string unit = "tokens") => ExecuteAsync($"""
        SET session_replication_role=replica;
        INSERT INTO billing_ai_quota_grants(id,organization_id,policy_version_id,quota_unit,quota_units,starts_at,ends_at,payment_transaction_id,provisioning_key,source_kind,topup_item_id)
        VALUES(gen_random_uuid(),'{org}',gen_random_uuid(),'{unit}',{units},now()-interval '1 hour',now()+interval '30 days',gen_random_uuid(),'fixture:'||gen_random_uuid(),'TopUp',gen_random_uuid());
        SET session_replication_role=origin;
        """);

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private async Task<Guid> ApprovedSource(IOrganizationAiGates gates, Guid admin, string visibility = "Common", Guid? org = null)
    {
        var created = await gates.AdminAsync("CreateSource", adminId, admin, Guid.Empty,
            new CreateKnowledgeSourceRequest(visibility, "QCVN 06 " + Guid.NewGuid().ToString("N")[..6], "2022", Sha(Guid.NewGuid().ToString()), org), Guid.NewGuid().ToString("N"), null, default);
        Assert.True(created.IsSuccess, created.Error?.Code);
        var id = created.Value.GetProperty("id").GetGuid();
        Assert.True((await gates.AdminAsync("ApproveSource", adminId, admin, id, new { }, null, 1, default)).IsSuccess);
        return id;
    }

    private static OrganizationAiWorker AiWorker(string runtime, FakeFastApi fake, int maxAttempts = 3)
    {
        var options = Options.Create(new OrganizationAiOptions { Enabled = true, BaseUrl = "https://ai.fixture.test/", MaxAttempts = maxAttempts, BackoffSeconds = 1, TimeoutSeconds = 5, LeaseSeconds = 30 });
        var services = new ServiceCollection();
        services.AddScoped(_ => BuildingContext(runtime));
        services.AddScoped<IOrganizationAiGates, OrganizationAiGates>();
        services.AddScoped<IAiProviderClient>(_ => new FastApiAiClient(fake, options));
        return new OrganizationAiWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), options, NullLogger<OrganizationAiWorker>.Instance);
    }

    private static object AnswerInput(Guid source, string question = "Where is the safe exit?", string kind = "KnowledgeSource") =>
        new { operation = "Answer", request = new { question, sources = new[] { new { kind, id = source } } } };

    private async Task<long> AllocationUnits(Guid request, string status, string column = "reserved_units") =>
        (long)(await ScalarAsync($"SELECT COALESCE(sum({column}),0)::bigint FROM billing_ai_quota_allocations WHERE request_id='{request}' AND status='{status}'"))!;

    [PostgresFact]
    public async Task Organization_ai_pins_policy_reserves_quota_and_settles_a_valid_result_once()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant(); var other = await SeedAiTenant();
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            var source = await ApprovedSource(gates, admin);
            var otherTenantSource = await ApprovedSource(gates, admin, "Organization", other.Org);
            Assert.Equal("AI_POLICY_UNAVAILABLE", (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source), "no-policy", default)).Error?.Code);
            var policy = await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("Answer", "tokens", 100, 1000, 5), "policy", null, default);
            Assert.True(policy.IsSuccess, policy.Error?.Code);
            Assert.Equal("AI_POLICY_OVERLAP", (await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("Answer", "tokens", 50, 1000, 5), "policy-2", null, default)).Error?.Code);
            Assert.Equal("FORBIDDEN", (await gates.AdminAsync("ListPolicies", tenant.User, tenant.Family, Guid.Empty, new { }, null, null, default)).Error?.Code);
            Assert.Equal("AI_QUOTA_EXHAUSTED", (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source), "no-quota", default)).Error?.Code);
            await SeedAiGrant(tenant.Org, 180);
            await SeedAiGrant(tenant.Org, 1000, "images");
            Assert.Equal("AI_SOURCE_NOT_ALLOWED", (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(otherTenantSource), "foreign", default)).Error?.Code);
            Assert.Equal("AI_INPUT_TOO_LARGE", (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, new string('a', 1001)), "large", default)).Error?.Code);
            var sources = (await gates.OrganizationAsync("Sources", tenant.User, tenant.Family, Guid.Empty, new { }, null, default)).Value.GetProperty("items");
            Assert.Contains(sources.EnumerateArray(), x => x.GetProperty("id").GetGuid() == source);
            Assert.DoesNotContain(sources.EnumerateArray(), x => x.GetProperty("id").GetGuid() == otherTenantSource);

            var submitted = await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source), "first", default);
            Assert.True(submitted.IsSuccess, submitted.Error?.Code);
            var id = submitted.Value.GetProperty("requestId").GetGuid();
            Assert.Equal("Queued", submitted.Value.GetProperty("status").GetString());
            Assert.Equal(100L, await AllocationUnits(id, "Reserved"));
            Assert.Equal(id, (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source), "first", default)).Value.GetProperty("requestId").GetGuid());
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Other question?"), "first", default)).Error?.Code);
            Assert.Equal("AI_QUOTA_EXHAUSTED", (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source), "second", default)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await gates.OrganizationAsync("Get", other.User, other.Family, id, new { }, null, default)).Error?.Code);
            await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{tenant.Family}'");
            Assert.Equal("UNAUTHORIZED", (await gates.OrganizationAsync("Get", tenant.User, tenant.Family, id, new { }, null, default)).Error?.Code);
            var family = Guid.NewGuid();
            await ExecuteAsync($"INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{tenant.User}','{family}','{Guid.NewGuid():N}{Guid.NewGuid():N}',now(),now()+interval '1 hour')");

            var fake = new FakeFastApi { Respond = (_, body) => FakeFastApi.Json(FakeFastApi.Answer(body!["requestId"]!.ToString(), source, 60)) };
            var worker = AiWorker(runtime, fake);
            Assert.True(await worker.RunOnceAsync(default));
            Assert.False(await worker.RunOnceAsync(default));
            var call = Assert.Single(fake.Calls);
            Assert.Equal("/v1/organization/answer", call.Path);
            Assert.Equal(id.ToString(), call.RequestId);
            Assert.Equal(source.ToString(), call.Body!["sources"]![0]!["id"]!.ToString());
            Assert.Equal(100, call.Body["limits"]!["maxUnits"]!.GetValue<int>());
            var done = (await gates.OrganizationAsync("Get", tenant.User, family, id, new { }, null, default)).Value;
            Assert.Equal("Succeeded", done.GetProperty("status").GetString());
            Assert.Equal(60, done.GetProperty("consumedUnits").GetInt32());
            Assert.Equal("Use the nearest protected stair.", done.GetProperty("result").GetProperty("answer").GetString());
            Assert.False(done.TryGetProperty("providerResult", out _));
            Assert.Equal(60L, await AllocationUnits(id, "Settled", "consumed_units"));
            Assert.Equal(0L, await AllocationUnits(id, "Reserved"));

            // A replayed completion with the old lease cannot settle twice.
            var replay = await gates.WorkerAsync("Complete", "replayer", id, Guid.NewGuid(), new { requestId = id, outcome = "Succeeded", result = new { answer = "x" }, citations = new[] { new { kind = "KnowledgeSource", id = source } }, usage = new { quotaUnit = "tokens", units = 1 } }, default);
            Assert.Equal("AI_LEASE_LOST", replay.GetProperty("code").GetString());
            Assert.Equal(60L, (long)(await ScalarAsync($"SELECT COALESCE(sum(consumed_units),0)::bigint FROM billing_ai_quota_allocations WHERE organization_id='{tenant.Org}'"))!);
            // 180 - 60 settled leaves room for the next 100-unit reservation.
            Assert.True((await gates.OrganizationAsync("Submit", tenant.User, family, Guid.Empty, AnswerInput(source), "second", default)).IsSuccess);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='AiRequest' AND target_id='{id}'"));
        });
    }

    [PostgresFact]
    public async Task Organization_ai_last_quota_race_reserves_for_exactly_one_request()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant();
        var second = Guid.NewGuid(); var secondFamily = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO users(id,organization_id,email,full_name,role,is_active,email_verified_at,created_at,updated_at) VALUES('{second}','{tenant.Org}','ai2-{second:N}@example.test','AI user 2','OrganizationUser',true,now(),now(),now()); INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{second}','{secondFamily}','{Guid.NewGuid():N}{Guid.NewGuid():N}',now(),now()+interval '1 hour')");
        await SeedAiGrant(tenant.Org, 100);
        await WithAiRuntime(async runtime =>
        {
            Guid source;
            await using (var db = BuildingContext(runtime))
            {
                var gates = new OrganizationAiGates(db);
                source = await ApprovedSource(gates, admin);
                Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("Answer", "tokens", 100, 1000, 5), "race-policy", null, default)).IsSuccess);
            }
            async Task<string> Submit(Guid user, Guid family, string key)
            {
                await using var db = BuildingContext(runtime);
                var result = await new OrganizationAiGates(db).OrganizationAsync("Submit", user, family, Guid.Empty, AnswerInput(source), key, default);
                return result.IsSuccess ? "OK" : result.Error!.Code;
            }
            var results = await Task.WhenAll(Submit(tenant.User, tenant.Family, "race-a"), Submit(second, secondFamily, "race-b"));
            Assert.Equal(["AI_QUOTA_EXHAUSTED", "OK"], results.Order().ToArray());
            Assert.Equal(100L, (long)(await ScalarAsync($"SELECT sum(reserved_units)::bigint FROM billing_ai_quota_allocations WHERE organization_id='{tenant.Org}' AND status='Reserved'"))!);
        });
    }

    [PostgresFact]
    public async Task Organization_ai_unknown_outcomes_wait_for_reconcile_and_never_resend()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant();
        await SeedAiGrant(tenant.Org, 1000);
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            var source = await ApprovedSource(gates, admin);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("Answer", "tokens", 100, 1000, 5), "p", null, default)).IsSuccess);
            var id = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source), "timeout", default)).Value.GetProperty("requestId").GetGuid();
            var committed = new HashSet<string>();
            var fake = new FakeFastApi();
            fake.Respond = (request, body) =>
            {
                // The provider commits the work, then the connection drops before the response arrives.
                if (request.Method == HttpMethod.Post) { committed.Add(body!["requestId"]!.ToString()); throw new HttpRequestException(HttpRequestError.ResponseEnded, "dropped"); }
                var requested = request.RequestUri!.Segments[^1];
                return committed.Contains(requested) ? FakeFastApi.Json(FakeFastApi.Answer(requested, source, 40)) : new(HttpStatusCode.NotFound);
            };
            var worker = AiWorker(runtime, fake);
            Assert.True(await worker.RunOnceAsync(default));
            var parked = (await gates.AdminAsync("GetRequest", adminId, admin, id, new { }, null, null, default)).Value;
            Assert.Equal("NeedsReconcile", parked.GetProperty("status").GetString());
            Assert.Equal("AI_OUTCOME_UNKNOWN", parked.GetProperty("failureCode").GetString());
            Assert.Equal(100L, await AllocationUnits(id, "Reserved"));
            Assert.False(await worker.RunOnceAsync(default));
            Assert.Equal("NOT_FOUND", (await gates.AdminAsync("Reconcile", adminId, admin, Guid.NewGuid(), new { }, "missing", null, default)).Error?.Code);
            Assert.Equal("FORBIDDEN", (await gates.AdminAsync("Reconcile", tenant.User, tenant.Family, id, new { }, "org", null, default)).Error?.Code);
            var scheduled = await gates.AdminAsync("Reconcile", adminId, admin, id, new { }, "reconcile", null, default);
            Assert.True(scheduled.IsSuccess, scheduled.Error?.Code);
            Assert.Equal(100L, await AllocationUnits(id, "Reserved"));
            Assert.True(await worker.RunOnceAsync(default));
            Assert.Equal(1, fake.Calls.Count(x => x.Method == HttpMethod.Post));
            Assert.Equal($"/v1/requests/{id}", fake.Calls.Last().Path);
            Assert.Equal("Succeeded", (await gates.OrganizationAsync("Get", tenant.User, tenant.Family, id, new { }, null, default)).Value.GetProperty("status").GetString());
            Assert.Equal(40L, await AllocationUnits(id, "Settled", "consumed_units"));
            Assert.Equal("AI_RECONCILE_NOT_ALLOWED", (await gates.AdminAsync("Reconcile", adminId, admin, id, new { }, "again", null, default)).Error?.Code);

            // A worker that dies mid-dispatch leaves the request Running; lease expiry parks it instead of re-sending.
            var crashed = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Crash?"), "crash", default)).Value.GetProperty("requestId").GetGuid();
            Assert.Equal("OK", (await gates.WorkerAsync("Claim", "dying", null, null, new { leaseSeconds = 30 }, default)).GetProperty("code").GetString());
            await ExecuteAsync($"UPDATE ai_requests SET lease_until=now()-interval '1 second' WHERE id='{crashed}'");
            Assert.False(await worker.RunOnceAsync(default));
            Assert.Equal("NeedsReconcile", (string?)await ScalarAsync($"SELECT status FROM ai_requests WHERE id='{crashed}'"));
            // Lookup that finds no record proves nothing was consumed: release, fail, still no second dispatch.
            Assert.True((await gates.AdminAsync("Reconcile", adminId, admin, crashed, new { }, "crash-reconcile", null, default)).IsSuccess);
            Assert.True(await worker.RunOnceAsync(default));
            Assert.Equal("Failed", (string?)await ScalarAsync($"SELECT status FROM ai_requests WHERE id='{crashed}'"));
            Assert.Equal("AI_REQUEST_NOT_RECEIVED", (string?)await ScalarAsync($"SELECT failure_code FROM ai_requests WHERE id='{crashed}'"));
            Assert.Equal(100L, await AllocationUnits(crashed, "Released"));
            Assert.Equal(1, fake.Calls.Count(x => x.Method == HttpMethod.Post));
        });
    }

    [PostgresFact]
    public async Task Organization_ai_rejects_foreign_citations_and_usage_beyond_reservation_and_retries_undelivered_calls()
    {
        var admin = await AdminFamily(); var tenant = await SeedAiTenant(); var other = await SeedAiTenant();
        await SeedAiGrant(tenant.Org, 1000);
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            var source = await ApprovedSource(gates, admin);
            var foreign = await ApprovedSource(gates, admin, "Organization", other.Org);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("Answer", "tokens", 100, 1000, 5), "p", null, default)).IsSuccess);
            var citation = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Cite?"), "cite", default)).Value.GetProperty("requestId").GetGuid();
            var fake = new FakeFastApi { Respond = (_, body) => FakeFastApi.Json(FakeFastApi.Answer(body!["requestId"]!.ToString(), foreign, 10)) };
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            var org = (await gates.OrganizationAsync("Get", tenant.User, tenant.Family, citation, new { }, null, default)).Value;
            Assert.Equal("NeedsReconcile", org.GetProperty("status").GetString());
            Assert.Equal("AI_CITATION_INVALID", org.GetProperty("failureCode").GetString());
            Assert.False(org.TryGetProperty("result", out _));
            Assert.False(org.TryGetProperty("providerResult", out _));
            Assert.True((await gates.AdminAsync("GetRequest", adminId, admin, citation, new { }, null, null, default)).Value.TryGetProperty("providerResult", out _));
            Assert.Equal(100L, await AllocationUnits(citation, "Reserved"));

            var over = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Over?"), "over", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, body) => FakeFastApi.Json(FakeFastApi.Answer(body!["requestId"]!.ToString(), source, 101));
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal("AI_USAGE_EXCEEDS_RESERVATION", (string?)await ScalarAsync($"SELECT failure_code FROM ai_requests WHERE id='{over}'"));
            Assert.Equal(100L, await AllocationUnits(over, "Reserved"));

            var malformed = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Markup?"), "markup", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, body) => FakeFastApi.Json(new { requestId = body!["requestId"]!.ToString(), outcome = "Succeeded", answer = "<script>x</script>", citations = new[] { new { kind = "KnowledgeSource", id = source } }, usage = new { quotaUnit = "tokens", units = 5 } });
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal("AI_RESULT_INVALID", (string?)await ScalarAsync($"SELECT failure_code FROM ai_requests WHERE id='{malformed}'"));

            var safety = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Unsafe?"), "safety", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, body) => FakeFastApi.Json(new { requestId = body!["requestId"]!.ToString(), outcome = "SafetyRejected", message = "Request declined.", citations = Array.Empty<object>(), usage = new { quotaUnit = "tokens", units = 3 } });
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            var rejected = (await gates.OrganizationAsync("Get", tenant.User, tenant.Family, safety, new { }, null, default)).Value;
            Assert.Equal("SafetyRejected", rejected.GetProperty("status").GetString());
            Assert.Equal(3, rejected.GetProperty("consumedUnits").GetInt32());
            Assert.Equal(3L, await AllocationUnits(safety, "Settled", "consumed_units"));
            Assert.Equal(0L, await AllocationUnits(safety, "Reserved"));

            var refused = (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, AnswerInput(source, "Down?"), "down", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
            Assert.True(await AiWorker(runtime, fake, maxAttempts: 2).RunOnceAsync(default));
            Assert.Equal("Queued", (string?)await ScalarAsync($"SELECT status FROM ai_requests WHERE id='{refused}'"));
            await ExecuteAsync($"UPDATE ai_requests SET next_attempt_at=now()-interval '1 second' WHERE id='{refused}'");
            Assert.True(await AiWorker(runtime, fake, maxAttempts: 2).RunOnceAsync(default));
            Assert.Equal("AI_PROVIDER_UNREACHABLE", (string?)await ScalarAsync($"SELECT failure_code FROM ai_requests WHERE id='{refused}' AND status='Failed'"));
            Assert.Equal(100L, await AllocationUnits(refused, "Released"));

            var draftPolicy = await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("ScenarioDraft", "tokens", 200, 2000, 5), "draft-policy", null, default);
            Assert.True(draftPolicy.IsSuccess);
            Assert.Equal("NOT_FOUND", (await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, new { operation = "ScenarioDraft", request = new { buildingId = other.Building, prompt = "Draft", sources = Array.Empty<object>() } }, "foreign-building", default)).Error?.Code);
            var draft = await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, new { operation = "ScenarioDraft", request = new { buildingId = tenant.Building, prompt = "Draft a kitchen fire", sources = Array.Empty<object>() } }, "draft", default);
            Assert.True(draft.IsSuccess, draft.Error?.Code);
            Assert.DoesNotContain(draft.Value.GetProperty("sources").EnumerateArray(), x => x.GetProperty("id").GetGuid() == foreign);
            fake.Respond = (_, body) => FakeFastApi.Json(new { requestId = body!["requestId"]!.ToString(), outcome = "Succeeded", draft = new { state = Fire3D.Tests.Shared.EditorContractFixtures.State(), notes = "Starts in the kitchen." }, citations = new[] { new { kind = "KnowledgeSource", id = source } }, usage = new { quotaUnit = "tokens", units = 150 } });
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal("/v1/organization/scenario-draft", fake.Calls.Last().Path);
            Assert.Equal(tenant.Building.ToString(), fake.Calls.Last().Body!["building"]!["id"]!.ToString());
            var drafted = (await gates.OrganizationAsync("Get", tenant.User, tenant.Family, draft.Value.GetProperty("requestId").GetGuid(), new { }, null, default)).Value;
            Assert.Equal("Succeeded", drafted.GetProperty("status").GetString());
            Assert.Equal("fet3d.editor/1", drafted.GetProperty("result").GetProperty("draft").GetProperty("state").GetProperty("schemaVersion").GetString());
        });
    }

    [PostgresFact]
    public async Task Organization_ai_HTTP_requires_adapter_and_admin_source_registry_uses_etags()
    {
        var adminTokens = await LoginAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminTokens.AccessToken);
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/admin/knowledge-sources") { Content = JsonContent.Create(new { visibility = "Common", title = "QCVN 06:2022", versionLabel = "2022", sourceHash = Sha("qcvn"), sourceUri = "https://example.test/qcvn.pdf" }) };
        create.Headers.Add("Idempotency-Key", "source-http");
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/admin/knowledge-sources") { Content = JsonContent.Create(new { visibility = "Common", title = "<b>x</b>", versionLabel = "v 1", sourceHash = "abc", sourceUri = "http://x" }) };
        bad.Headers.Add("Idempotency-Key", "source-bad");
        var badResponse = await client.SendAsync(bad);
        Assert.Equal((HttpStatusCode)422, badResponse.StatusCode);
        var issues = await badResponse.Content.ReadAsStringAsync();
        Assert.Contains("MARKUP_NOT_ALLOWED", issues); Assert.Contains("URL_INVALID", issues);
        var approve = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/knowledge-sources/{id}/approve");
        Assert.Equal((HttpStatusCode)428, (await client.SendAsync(approve)).StatusCode);
        approve = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/knowledge-sources/{id}/approve"); approve.Headers.TryAddWithoutValidation("If-Match", "\"7\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(approve)).StatusCode);
        approve = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/knowledge-sources/{id}/approve"); approve.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var approved = await client.SendAsync(approve);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("\"2\"", approved.Headers.ETag!.Tag);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM integration_outbox_events WHERE payload->>'scope'='knowledge' AND aggregate_id='{id}'"));

        var tenant = await SeedAiTenant();
        var policy = new HttpRequestMessage(HttpMethod.Post, "/api/admin/ai/policies") { Content = JsonContent.Create(new { operation = "Answer", quotaUnit = "Tokens", reserveUnits = 0, maxInputChars = 10, maxSources = 1 }) };
        policy.Headers.Add("Idempotency-Key", "policy-bad");
        Assert.Equal((HttpStatusCode)422, (await client.SendAsync(policy)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/ai/requests?status=NeedsReconcile")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/ai/requests?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/ai/organization/answer", new { question = "Exit?" })).StatusCode);
        using var anonymous = factory!.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/ai/organization/answer", new { question = "Exit?" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/ai/requests/{Guid.NewGuid()}")).StatusCode);

        // The test host has no OrganizationAi adapter: valid input gets 503 and nothing is reserved; invalid input is still 422.
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{tenant.User}'");
        var orgTokens = await LoginAsync($"ai-{tenant.User:N}@example.test");
        using var org = factory.CreateClient();
        org.DefaultRequestHeaders.Authorization = new("Bearer", orgTokens.AccessToken);
        var answer = new HttpRequestMessage(HttpMethod.Post, "/api/ai/organization/answer") { Content = JsonContent.Create(new { question = "Where is the exit?" }) };
        answer.Headers.Add("Idempotency-Key", "answer-http");
        var unavailable = await org.SendAsync(answer);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("AI_PROVIDER_UNAVAILABLE", await unavailable.Content.ReadAsStringAsync());
        var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/ai/organization/scenario-draft") { Content = JsonContent.Create(new { buildingId = tenant.Building, prompt = "<iframe>", sources = new[] { new { kind = "Web", id = Guid.NewGuid() } } }) };
        invalid.Headers.Add("Idempotency-Key", "draft-http");
        var invalidBody = await (await org.SendAsync(invalid)).Content.ReadAsStringAsync();
        Assert.Contains("MARKUP_NOT_ALLOWED", invalidBody); Assert.Contains("VALUE_UNSUPPORTED", invalidBody);
        Assert.Equal(HttpStatusCode.OK, (await org.GetAsync("/api/ai/organization/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.GetAsync($"/api/ai/requests/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM ai_requests"));
    }
}
