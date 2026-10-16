using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Ai;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<(Guid User, Guid Family)> SeedOrganizationUser(Guid organization)
    {
        var user = Guid.NewGuid(); var family = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO users(password_hash,id,organization_id,email,full_name,role,is_active,email_verified_at,created_at,updated_at) VALUES('fixture-hash','{user}','{organization}','ai-{user:N}@example.test','AI user','OrganizationUser',true,now(),now(),now());
            INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at) VALUES(gen_random_uuid(),'{user}','{family}','{Guid.NewGuid():N}{Guid.NewGuid():N}',now(),now()+interval '1 hour');
            """);
        return (user, family);
    }

    private static object DraftResponse(string id, Guid artifact, Guid revision, Guid source, string anchor = "1hOSvn6df7F8_7GcBWlRGQ") => new
    {
        request_id = id, response_type = "ScenarioDraft", status = "NeedsUserEdit", recommendation = "Fire starts near the first door.",
        scenario_draft = new { state = PinnedState(artifact), notes = "Edit the evacuee count before saving." },
        bim_anchors = new[] { new { ifc_global_id = anchor, entity_type = "IfcDoor", revision_id = revision } },
        citations = new[] { new { document_id = source, document_version = source, excerpt_locator = "Article 3" } },
        confidence = "medium", assumptions = new[] { "Doors open outward." }, limitations = new[] { "An expert must check the site." },
        usage = new { provider_units = 150, model_version = "fixture-1" }
    };

    // #91: a scenario draft pins one revision with accepted geometry and sends its BIM facts; the result must anchor to them.
    [PostgresFact]
    public async Task Ai_scenario_draft_pins_bim_facts_and_rejects_drafts_that_leave_them()
    {
        var admin = await AdminFamily();
        var g = await SeedEditorGeometry();
        await SeedEditorCatalog();
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM buildings WHERE id='{g.Building}'"))!;
        var (user, family) = await SeedOrganizationUser(org);
        await SeedAiGrant(org, 1000);
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            var source = await ApprovedSource(gates, admin);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("ScenarioDraft", "tokens", 200, 2000, 5), "draft-policy", null, default)).IsSuccess);
            object Draft(Guid? revision = null) => new { operation = "ScenarioDraft", request = new { buildingId = g.Building, revisionId = revision, prompt = "Kitchen fire on the first floor", sources = Array.Empty<object>() } };
            Assert.Equal("NOT_FOUND", (await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(Guid.NewGuid()), "foreign-revision", default)).Error?.Code);

            var submitted = await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(), "draft-1", default);
            Assert.True(submitted.IsSuccess, submitted.Error?.Code);
            var id = submitted.Value.GetProperty("requestId").GetGuid();
            Assert.Equal(g.Revision, submitted.Value.GetProperty("input").GetProperty("revisionId").GetGuid());
            Assert.Equal(g.Building, submitted.Value.GetProperty("input").GetProperty("buildingId").GetGuid());
            var bim = (await gates.AdminAsync("GetRequest", adminId, admin, id, new { }, null, null, default)).Value.GetProperty("bim");
            Assert.Equal(g.Artifact, bim.GetProperty("geometry").GetProperty("artifactId").GetGuid());
            Assert.Equal(4, bim.GetProperty("anchors").GetArrayLength());
            Assert.Equal(2, bim.GetProperty("floors").GetArrayLength());
            Assert.Equal("1.4.0", bim.GetProperty("runtime").GetProperty("runtimeVersion").GetString());
            Assert.DoesNotContain(bim.GetProperty("qualityFlags").EnumerateArray(), x => x.GetProperty("code").GetString() == "EXIT_MAPPING_MISSING");

            var fake = new FakeFastApi { Respond = (_, body) => FakeFastApi.Json(DraftResponse(body!["request_id"]!.ToString(), g.Artifact, g.Revision, source)) };
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            var call = fake.Calls.Single();
            Assert.Equal("/v2/organization/scenario-draft", call.Path);
            Assert.Equal("ScenarioDraft", call.Body!["response_type"]!.GetValue<string>());
            Assert.Equal(g.Revision.ToString(), call.Body["bim"]!["revisionId"]!.ToString());
            Assert.Equal(source.ToString(), call.Body["sources"]![0]!["document_id"]!.ToString());
            var drafted = (await gates.OrganizationAsync("Get", user, family, id, new { }, null, default)).Value;
            Assert.Equal("Succeeded", drafted.GetProperty("status").GetString());
            Assert.Equal("ScenarioDraft", drafted.GetProperty("resultType").GetString());
            var result = drafted.GetProperty("result");
            Assert.Equal("NeedsUserEdit", result.GetProperty("status").GetString());
            Assert.Equal(g.Artifact, result.GetProperty("draft").GetProperty("state").GetProperty("geometry").GetProperty("artifactId").GetGuid());
            Assert.Equal("1hOSvn6df7F8_7GcBWlRGQ", result.GetProperty("bimAnchors")[0].GetProperty("ifcGlobalId").GetString());
            Assert.Equal("medium", result.GetProperty("confidence").GetString());
            Assert.Equal(150, drafted.GetProperty("consumedUnits").GetInt32());
            Assert.Equal("fixture-1", (string?)await ScalarAsync($"SELECT model_version FROM ai_requests WHERE id='{id}'"));

            // An anchor that is not in the pinned revision, and a knowledge answer where a draft was asked, are both parked.
            var stray = (await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(g.Revision), "draft-2", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, body) => FakeFastApi.Json(DraftResponse(body!["request_id"]!.ToString(), g.Artifact, g.Revision, source, anchor: "0000000000000000000000"));
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal("NeedsReconcile/AI_BIM_ANCHOR_INVALID", (string?)await ScalarAsync($"SELECT status||'/'||failure_code FROM ai_requests WHERE id='{stray}'"));
            var answered = (await gates.OrganizationAsync("Submit", user, family, Guid.Empty, Draft(g.Revision), "draft-3", default)).Value.GetProperty("requestId").GetGuid();
            fake.Respond = (_, body) => FakeFastApi.Json(FakeFastApi.Answer(body!["request_id"]!.ToString(), source, 10));
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal("NeedsReconcile/AI_RESULT_INVALID", (string?)await ScalarAsync($"SELECT status||'/'||failure_code FROM ai_requests WHERE id='{answered}'"));
            Assert.Equal(200L, await AllocationUnits(answered, "Reserved"));
        });
    }

    // #77: Trainee answers use Common/Learn sources and the Trainee's own context, a daily allowance, and no AI during Assessment.
    [PostgresFact]
    public async Task Trainee_ai_uses_daily_quota_learner_safe_context_and_stops_during_assessment()
    {
        var admin = await AdminFamily();
        var f = await PublishedTraining();
        var (trainee, traineeFamily) = await SeedTrainee();
        var (stranger, strangerFamily) = await SeedTrainee();
        var tenant = await SeedAiTenant();
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            var common = await ApprovedSource(gates, admin);
            var privateSource = await ApprovedSource(gates, admin, "Organization", tenant.Org);
            Assert.Equal("VALIDATION_ERROR", (await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("TraineeAnswer", "tokens", 100, 1000, 5), "no-daily", null, default)).Error?.Code);
            Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest("TraineeAnswer", "tokens", 100, 1000, 5, DailyUnits: 150), "daily", null, default)).IsSuccess);
            var policies = (await gates.AdminAsync("ListPolicies", adminId, admin, Guid.Empty, new { operation = "TraineeAnswer", page = 1, pageSize = 1 }, null, null, default)).Value;
            Assert.Equal(150, policies.GetProperty("items")[0].GetProperty("dailyUnits").GetInt32());
            Assert.Equal(1, policies.GetProperty("total").GetInt32());

            Task<Fire3D.Application.Authentication.AuthResult<JsonElement>> Ask(string key, object? context = null, Guid? as_ = null) =>
                gates.TraineeAsync("Submit", as_ ?? trainee, as_ == stranger ? strangerFamily : traineeFamily, Guid.Empty, new { question = "Why use the stairs?", sources = Array.Empty<object>(), context }, key, default);
            var quota = (await gates.TraineeAsync("Quota", trainee, traineeFamily, Guid.Empty, new { }, null, default)).Value;
            Assert.Equal(150, quota.GetProperty("remainingUnits").GetInt32());
            Assert.True(quota.GetProperty("canSubmit").GetBoolean());
            Assert.Equal("FORBIDDEN", (await gates.TraineeAsync("Quota", tenant.User, tenant.Family, Guid.Empty, new { }, null, default)).Error?.Code);
            Assert.Equal("AI_CONTEXT_NOT_FOUND", (await Ask("learn-missing", new { kind = "Learn", postId = Guid.NewGuid(), versionId = Guid.NewGuid() })).Error?.Code);
            Assert.Equal("AI_CONTEXT_NOT_FOUND", (await Ask("result-foreign", new { kind = "Result", sessionId = Guid.NewGuid() })).Error?.Code);

            var first = await Ask("general");
            Assert.True(first.IsSuccess, first.Error?.Code);
            var id = first.Value.GetProperty("requestId").GetGuid();
            Assert.Equal("trainee", first.Value.GetProperty("audience").GetString());
            Assert.Equal("Accepted", first.Value.GetProperty("status").GetString());
            var pinned = first.Value.GetProperty("sources").EnumerateArray().ToList();
            Assert.Contains(pinned, x => x.GetProperty("id").GetGuid() == common);
            Assert.DoesNotContain(pinned, x => x.GetProperty("id").GetGuid() == privateSource);
            Assert.Equal(id, (await Ask("general")).Value.GetProperty("requestId").GetGuid());
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", (await gates.TraineeAsync("Submit", trainee, traineeFamily, Guid.Empty, new { question = "Other?", sources = Array.Empty<object>(), context = (object?)null }, "general", default)).Error?.Code);
            // 100 reserved of 150: a second reservation does not fit today.
            Assert.Equal("AI_DAILY_QUOTA_EXHAUSTED", (await Ask("second")).Error?.Code);
            Assert.Equal("NOT_FOUND", (await gates.TraineeAsync("Get", stranger, strangerFamily, id, new { }, null, default)).Error?.Code);

            var fake = new FakeFastApi { Respond = (_, body) => FakeFastApi.Json(FakeFastApi.Answer(body!["request_id"]!.ToString(), common, 30)) };
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal("/v2/trainee/answer", fake.Calls.Last().Path);
            Assert.Equal("trainee", fake.Calls.Last().Body!["audience"]!.GetValue<string>());
            Assert.Null(fake.Calls.Last().Body!["organization_id"]);
            var answered = (await gates.TraineeAsync("Get", trainee, traineeFamily, id, new { }, null, default)).Value;
            Assert.Equal("Answered", answered.GetProperty("result").GetProperty("status").GetString());
            Assert.False(answered.GetProperty("resultWithheld").GetBoolean());
            Assert.Equal(30, (await gates.TraineeAsync("Quota", trainee, traineeFamily, Guid.Empty, new { }, null, default)).Value.GetProperty("usedUnits").GetInt32());

            // Training context: a learner-safe snapshot (no rubric) pinned as a citable source; hidden again once access is lost.
            var training = await Ask("training", new { kind = "Training", trainingId = f.Training });
            Assert.True(training.IsSuccess, training.Error?.Code);
            var trainingId = training.Value.GetProperty("requestId").GetGuid();
            var snapshot = training.Value.GetProperty("sources").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "TrainingSnapshot");
            Assert.False(snapshot.TryGetProperty("content", out _));
            fake.Respond = (_, body) => FakeFastApi.Json(FakeFastApi.Answer(body!["request_id"]!.ToString(), f.Training, 20, snapshot.GetProperty("versionId").GetGuid()));
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            var sent = fake.Calls.Last().Body!["sources"]!.AsArray().Single(x => x!["kind"]!.GetValue<string>() == "TrainingSnapshot")!["content"]!.ToJsonString();
            Assert.Contains("trainingName", sent);
            Assert.DoesNotContain("rubric", sent, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("TrainingSnapshot", (await gates.TraineeAsync("Get", trainee, traineeFamily, trainingId, new { }, null, default)).Value.GetProperty("citations")[0].GetProperty("kind").GetString());
            await ExecuteAsync($"UPDATE buildings SET visibility='Private' WHERE id='{f.Building}'");
            var withheld = (await gates.TraineeAsync("Get", trainee, traineeFamily, trainingId, new { }, null, default)).Value;
            Assert.True(withheld.GetProperty("resultWithheld").GetBoolean());
            Assert.Equal(JsonValueKind.Null, withheld.GetProperty("result").ValueKind);
            Assert.Equal(0, withheld.GetProperty("citations").GetArrayLength());
            Assert.Equal("AI_CONTEXT_NOT_FOUND", (await Ask("training-lost", new { kind = "Training", trainingId = f.Training })).Error?.Code);

            // Own saved result stays usable without Building access; a draft answer to a Trainee is not accepted.
            var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM trainings WHERE id='{f.Training}'"))!;
            await SeedAnalyticsSession(org, trainee, f.Training, "now()-interval '1 hour'", outcome: "Passed", resultAt: "now()-interval '30 minutes'");
            var session = (Guid)(await ScalarAsync($"SELECT id FROM sessions WHERE trainee_user_id='{trainee}'"))!;
            Assert.Equal("AI_CONTEXT_NOT_FOUND", (await Ask("result-stranger", new { kind = "Result", sessionId = session }, stranger)).Error?.Code);
            var result = await Ask("result", new { kind = "Result", sessionId = session });
            Assert.True(result.IsSuccess, result.Error?.Code);
            fake.Respond = (_, body) => FakeFastApi.Json(new { request_id = body!["request_id"]!.ToString(), response_type = "KnowledgeAnswer", status = "NeedsUserEdit", recommendation = "x", citations = Array.Empty<object>(), usage = new { provider_units = 1 } });
            Assert.True(await AiWorker(runtime, fake).RunOnceAsync(default));
            Assert.Equal("NeedsReconcile/AI_RESULT_INVALID", (string?)await ScalarAsync($"SELECT status||'/'||failure_code FROM ai_requests WHERE id='{result.Value.GetProperty("requestId").GetGuid()}'"));

            await SeedAnalyticsSession(org, trainee, f.Training, "now()", status: "Running", mode: "Assessment");
            Assert.Equal("AI_BLOCKED_DURING_ASSESSMENT", (await Ask("during-assessment")).Error?.Code);
            Assert.True((await gates.TraineeAsync("Quota", trainee, traineeFamily, Guid.Empty, new { }, null, default)).Value.GetProperty("blockedByAssessment").GetBoolean());
            var list = (await gates.TraineeAsync("List", trainee, traineeFamily, Guid.Empty, new { page = 1, pageSize = 20 }, null, default)).Value;
            Assert.Equal(3, list.GetProperty("total").GetInt32());
            Assert.Equal(1, (await gates.AdminAsync("ListRequests", adminId, admin, Guid.Empty, new { audience = "trainee", status = "NeedsReconcile" }, null, null, default)).Value.GetProperty("total").GetInt32());
        });

        // HTTP: Trainee routes are Trainee-only and need the adapter before anything is reserved.
        var login = (await LoginAsync($"trainee-{trainee:N}@example.test")).AccessToken;
        using var http = factory!.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", login);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/ai/trainee/quota")).StatusCode);
        var ask = new HttpRequestMessage(HttpMethod.Post, "/api/ai/trainee/answer") { Content = JsonContent.Create(new { question = "Why?", context = new { kind = "Training" } }) };
        ask.Headers.Add("Idempotency-Key", "http-invalid");
        Assert.Equal((HttpStatusCode)422, (await http.SendAsync(ask)).StatusCode);
        ask = new HttpRequestMessage(HttpMethod.Post, "/api/ai/trainee/answer") { Content = JsonContent.Create(new { question = "Why?" }) };
        ask.Headers.Add("Idempotency-Key", "http-unavailable");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.SendAsync(ask)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/ai/trainee/requests?status=Succeeded")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/ai/trainee/quota")).StatusCode);
    }

    // #91: knowledge_chunks follows v7 where pgvector exists; only fet3d_ai_indexer reads or writes it.
    [PostgresFact]
    public async Task Knowledge_chunks_are_created_with_pgvector_and_only_the_indexer_role_uses_them()
    {
        if ((bool)(await ScalarAsync("SELECT to_regtype('vector') IS NULL"))!)
        {
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM pg_class WHERE relname='knowledge_chunks' AND relkind='r'"));
            // Stand-in for the extension type so the conditional DDL runs exactly as on Supabase.
            await ExecuteAsync("CREATE SCHEMA IF NOT EXISTS extensions; CREATE DOMAIN extensions.vector AS real[];");
            using var stream = typeof(Fire3D.Infrastructure.Migrations.AddAiContractV2).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.KnowledgeChunks.sql")!;
            await ExecuteAsync(new StreamReader(stream).ReadToEnd());
        }
        var source = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO knowledge_sources(id,visibility,title,version_label,source_hash,approval_status,approved_by,approved_at,created_by,created_at) VALUES('{source}','Common','QCVN','2022','{new string('c', 64)}','Approved','{adminId}',now(),'{adminId}',now())");
        foreach (var role in new[] { "fire3d_api", "fet3d_backend_executor" })
            Assert.Equal(false, await ScalarAsync($"SELECT has_table_privilege('{role}','knowledge_chunks','SELECT')"));
        var login = "ai_indexer_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}' IN ROLE fet3d_ai_indexer;GRANT USAGE ON SCHEMA public TO {login}");
        try
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using (var insert = new NpgsqlCommand($"INSERT INTO knowledge_chunks(source_id,chunk_index,content,locator) VALUES('{source}',0,'Article 3','{{\"section\":\"3\"}}')", connection))
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            await using (var states = new NpgsqlCommand($"SELECT eligible FROM ai_source_states WHERE kind='KnowledgeSource' AND id='{source}'", connection))
                Assert.Equal(true, await states.ExecuteScalarAsync());
            await using var forbidden = new NpgsqlCommand("SELECT count(*) FROM knowledge_sources", connection);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteScalarAsync())).SqlState);
        }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }
}
