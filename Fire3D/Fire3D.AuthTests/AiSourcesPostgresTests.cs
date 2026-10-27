using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Application.Content;
using Fire3D.Infrastructure.Ai;
using Fire3D.Infrastructure.Content;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<Guid> CommonSource(string title)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO knowledge_sources(id,visibility,title,version_label,source_hash,approval_status,approved_by,approved_at,created_by,created_at) VALUES('{id}','Common','{title}','2025','{Sha(id.ToString())}','Approved','{adminId}',now(),'{adminId}',now())");
        return id;
    }

    private static List<Guid> PinnedIds(JsonElement request) => request.GetProperty("sources").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();

    // #178: with no sources named, the pinned ones are those that fit the question (and the lesson being read), not the first by title.
    [PostgresFact]
    public async Task Ai_pins_the_sources_the_lesson_cites_and_those_relevant_to_the_question()
    {
        var admin = await AdminFamily();
        var trainee = await SeedTrainee();
        var tenant = await SeedAiTenant();
        await SeedAiGrant(tenant.Org, 1000, "requests");
        // Alphabetically first, and unrelated to the question.
        await CommonSource("American Red Cross home fire guide");
        await CommonSource("GOV.UK fire safety in the home");
        await CommonSource("Home Office fire risk guidance");
        var charging = await CommonSource("QCVN 25:2025 trạm sạc xe máy điện");
        var decree = await CommonSource("Zulu NĐ 105/2025 hướng dẫn PCCC");
        Guid post = Guid.Empty, version = Guid.Empty;
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var content = LearnContent.Normalize(LearnInput("Thoát hiểm khi cháy chung cư", sources: [new(decree, "Primary")])).Content!;
            var created = await new ContentGates(db).LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug = "thoat-hiem-chung-cu", version = content, publish = true }, "post", null, default);
            Assert.True(created.IsSuccess, created.Error?.Code);
            post = created.Value.GetProperty("id").GetGuid();
            version = created.Value.GetProperty("versions")[0].GetProperty("id").GetGuid();
        });
        await WithAiRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new OrganizationAiGates(db);
            foreach (var (operation, daily) in new[] { ("Answer", (int?)null), ("TraineeAnswer", 20) })
                Assert.True((await gates.AdminAsync("CreatePolicy", adminId, admin, Guid.Empty, new CreateAiPolicyRequest(operation, "requests", 1, 2000, 2, DailyUnits: daily), "p-" + operation, null, default)).IsSuccess);
            const string question = "Sạc xe máy điện qua đêm có an toàn không?";

            // Organization: the charging regulation outranks the alphabetically earlier guides.
            var asked = await gates.OrganizationAsync("Submit", tenant.User, tenant.Family, Guid.Empty, new { operation = "Answer", request = new { question, sources = Array.Empty<object>() } }, "org-1", default);
            Assert.True(asked.IsSuccess, asked.Error?.Code);
            var pinned = PinnedIds(asked.Value);
            Assert.Equal(2, pinned.Count);
            Assert.Equal(charging, pinned[0]);

            // Trainee reading the lesson: the lesson, the decree it cites (though nothing in the question names it), then the regulation.
            Task<Fire3D.Application.Authentication.AuthResult<JsonElement>> Ask(string key, object? context) =>
                gates.TraineeAsync("Submit", trainee.Id, trainee.Family, Guid.Empty, new { question, sources = Array.Empty<object>(), context }, key, default);
            var inLesson = await Ask("lesson", new { kind = "Learn", postId = post, versionId = version });
            Assert.True(inLesson.IsSuccess, inLesson.Error?.Code);
            Assert.Equal([post, decree, charging], PinnedIds(inLesson.Value));
            // Outside a lesson only relevance orders them.
            Assert.Equal(charging, PinnedIds((await Ask("general", null)).Value)[0]);
            // A named source list is still honoured as before.
            var named = await gates.TraineeAsync("Submit", trainee.Id, trainee.Family, Guid.Empty, new { question, sources = new[] { new { kind = "KnowledgeSource", id = decree } }, context = (object?)null }, "named", default);
            Assert.Equal([decree], PinnedIds(named.Value));
        });

        // ScenarioDraft takes Library versions before documents, whatever the words.
        var item = Guid.NewGuid(); var libraryVersion = Guid.NewGuid();
        await ExecuteAsync($"""
            SET session_replication_role=replica;
            INSERT INTO organization_library_items(id,kind,code,is_active,created_by,created_at,updated_at) VALUES('{item}','ScenarioTemplate','TPL-ZZ',true,'{adminId}',now(),now());
            INSERT INTO organization_library_versions(id,item_id,version_number,name,payload,payload_hash,status,published_by,published_at,created_by,created_at,updated_at)
            VALUES('{libraryVersion}','{item}',1,'Mẫu cháy bếp',jsonb_build_object(),'{Sha(item.ToString())}','Published','{adminId}',now(),'{adminId}',now(),now());
            SET session_replication_role=origin;
            """);
        var draft = (string)(await ScalarAsync($"SELECT ai_pick_sources('{tenant.Org}',false,'ScenarioDraft','Sạc xe máy điện','[]','[]',NULL,2)::text"))!;
        var draftIds = JsonDocument.Parse(draft).RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([libraryVersion, charging], draftIds);
        var answer = (string)(await ScalarAsync($"SELECT ai_pick_sources('{tenant.Org}',false,'Answer','Sạc xe máy điện','[]','[]',NULL,1)::text"))!;
        Assert.Equal(charging, JsonDocument.Parse(answer).RootElement[0].GetProperty("id").GetGuid());
        // The helpers are internal to the gates.
        foreach (var role in new[] { "fire3d_api", "fet3d_backend_executor" })
            Assert.Equal(false, await ScalarAsync($"SELECT CASE WHEN EXISTS(SELECT 1 FROM pg_roles WHERE rolname='{role}') THEN has_function_privilege('{role}','ai_pick_sources(uuid,boolean,text,text,jsonb,jsonb,uuid,integer)','EXECUTE') ELSE false END"));
    }

    // BE#179: an uploaded document becomes a Draft source whose sourceUri is the API's file address and whose hash the API computed;
    // the file row is private, never deleted and immutable once stored.
    [PostgresFact]
    public async Task Knowledge_source_files_register_a_source_from_the_stored_bytes()
    {
        var admin = await AdminFamily();
        var pdf = "%PDF-1.7\n% Fire3D guide\n%%EOF"u8.ToArray();
        Guid uploadId, source;
        using (var scope = factory!.Services.CreateScope())
        {
            var service = new KnowledgeSourceFileService(scope.ServiceProvider.GetRequiredService<IKnowledgeSourceFileStore>(),
                new KnowledgeSourceFileServiceTests.Storage(pdf, "application/pdf"), scope.ServiceProvider.GetRequiredService<IOrganizationAiGates>(), TimeProvider.System);
            uploadId = (await service.CreateUploadIntentAsync(adminId, "guide-1", new("huong-dan.pdf", "application/pdf", pdf.Length), default)).Value!.UploadId;
            var details = new CompleteKnowledgeSourceUploadRequest("Common", "Hướng dẫn sử dụng FET3D", "2026.10");
            var done = await service.CompleteUploadAsync(adminId, admin, uploadId, details, "https://api.fet3d.test", default);
            Assert.True(done.IsSuccess, done.Error?.Code);
            source = done.Value.GetProperty("id").GetGuid();
            Assert.Equal(("Draft", $"https://api.fet3d.test/api/knowledge-sources/files/{uploadId}", Convert.ToHexStringLower(SHA256.HashData(pdf))),
                (done.Value.GetProperty("approvalStatus").GetString(), done.Value.GetProperty("sourceUri").GetString(), done.Value.GetProperty("sourceHash").GetString()));
            Assert.Equal(source, (await scope.ServiceProvider.GetRequiredService<IKnowledgeSourceFileStore>().FindAsync(uploadId, default))!.SourceId);
            // Completing again replays the same source; the same bytes and label cannot be registered a second time through another upload.
            Assert.Equal(source, (await service.CompleteUploadAsync(adminId, admin, uploadId, details, "https://api.fet3d.test", default)).Value.GetProperty("id").GetGuid());
            var again = (await service.CreateUploadIntentAsync(adminId, "guide-2", new("huong-dan.pdf", "application/pdf", pdf.Length), default)).Value!.UploadId;
            Assert.Equal("KNOWLEDGE_SOURCE_EXISTS", (await service.CompleteUploadAsync(adminId, admin, again, details, "https://api.fet3d.test", default)).Error?.Code);
        }
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='KnowledgeSource' AND target_id='{source}' AND action='Create'"));
        // Stored file and its link never change, even for the table owner; Supabase's public roles cannot read the table.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE knowledge_source_files SET file_name='x.pdf' WHERE id='{uploadId}'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE knowledge_source_files SET sha256='{new string('0', 64)}' WHERE id='{uploadId}'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"UPDATE knowledge_source_files SET source_id=NULL WHERE id='{uploadId}'"));
        Assert.True((bool)(await ScalarAsync("SELECT relrowsecurity FROM pg_class WHERE oid='public.knowledge_source_files'::regclass"))!);
        Assert.True((bool)(await ScalarAsync("""
            SELECT bool_and(NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=r) OR NOT has_table_privilege(r,'public.knowledge_source_files','SELECT,INSERT,UPDATE,DELETE'))
            FROM unnest(ARRAY['anon','authenticated']) r
            """))!);
        Assert.False((bool)(await ScalarAsync("SELECT has_table_privilege('fire3d_api','public.knowledge_source_files','DELETE')"))!);

        // HTTP: PlatformAdmin only, except the public file address; details are validated before anything is stored.
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/knowledge-sources/files/{Guid.NewGuid()}")).StatusCode);
        var body = new { fileName = "guide.md", contentType = "text/markdown", sizeBytes = 10 };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/knowledge-sources/upload-intent", body)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var noKey = await client.PostAsJsonAsync("/api/admin/knowledge-sources/upload-intent", body);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await noKey.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        var word = new HttpRequestMessage(HttpMethod.Post, "/api/admin/knowledge-sources/upload-intent") { Content = JsonContent.Create(body with { contentType = "application/msword" }) };
        word.Headers.Add("Idempotency-Key", "word");
        var rejected = await client.SendAsync(word);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("VALIDATION_ERROR", (await rejected.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        var bad = await client.PostAsJsonAsync($"/api/admin/knowledge-sources/uploads/{Guid.NewGuid()}/complete", new { visibility = "Common", title = "", versionLabel = "a b" });
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/knowledge-sources/uploads/{Guid.NewGuid()}/complete", new { visibility = "Common", title = "Guide", versionLabel = "1" })).StatusCode);
    }
}
