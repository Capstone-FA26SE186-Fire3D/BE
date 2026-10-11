using System.Text.Json;
using Fire3D.Infrastructure.Scenarios;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Scenarios;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Publish_forward_migration_restores_non_superuser_owner_membership_and_schema_privileges()
    {
        var login="publish_migration_"+Guid.NewGuid().ToString("N");
        // Migration identity can manage schema/owner grants; runtime identities cannot.
        await ExecuteAsync($"CREATE ROLE {login} LOGIN CREATEROLE NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}'; GRANT USAGE ON SCHEMA public TO {login}; GRANT CREATE ON SCHEMA public TO {login} WITH GRANT OPTION; GRANT fet3d_ifc_upload_owner TO {login} WITH ADMIN TRUE, SET FALSE, INHERIT FALSE");
        try
        {
            using var resource=typeof(Fire3D.Infrastructure.Releases.ReleaseWriteStore).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ReleasePublishReceipts.sql")!;
            var sql=await new StreamReader(resource).ReadToEndAsync();
            await using var connection=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Pooling=false}.ConnectionString);
            await connection.OpenAsync();await using var transaction=await connection.BeginTransactionAsync();
            await using var command=new NpgsqlCommand(sql,connection,transaction);await command.ExecuteNonQueryAsync();await transaction.CommitAsync();
            Assert.Equal(false,await ScalarAsync($"SELECT pg_has_role('{login}','fet3d_ifc_upload_owner','SET')"));
            Assert.Equal(false,await ScalarAsync($"SELECT pg_has_role('{login}','fet3d_ifc_upload_owner','USAGE')"));
            Assert.Equal(false,await ScalarAsync("SELECT has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE')"));
            Assert.Equal("fet3d_ifc_upload_owner",await ScalarAsync("SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid='publish_release_gate(text,uuid,uuid,uuid,jsonb,text,bigint)'::regprocedure"));
            await ExecuteAsync("DROP FUNCTION scenario_review_read_gate(text,uuid,uuid,uuid,jsonb,text,bigint);DROP FUNCTION scenario_review_summary(uuid)");
            using var readsResource=typeof(ScenarioReviewQueries).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ScenarioReviewReads.sql")!;
            var readsSql=await new StreamReader(readsResource).ReadToEndAsync();
            await using var readsTransaction=await connection.BeginTransactionAsync();
            await using var readsCommand=new NpgsqlCommand(readsSql,connection,readsTransaction);await readsCommand.ExecuteNonQueryAsync();await readsTransaction.CommitAsync();
            Assert.Equal(false,await ScalarAsync($"SELECT pg_has_role('{login}','fet3d_ifc_upload_owner','SET')"));
            Assert.Equal(false,await ScalarAsync($"SELECT pg_has_role('{login}','fet3d_ifc_upload_owner','USAGE')"));
            Assert.Equal(false,await ScalarAsync("SELECT has_schema_privilege('fet3d_ifc_upload_owner','public','CREATE')"));
            Assert.Equal("fet3d_ifc_upload_owner",await ScalarAsync("SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid='scenario_review_read_gate(text,uuid,uuid,uuid,jsonb,text,bigint)'::regprocedure"));
        }
        finally {await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}");}
    }

    [PostgresFact]
    public async Task Publish_issue_returns_pinned_response_and_replays_client_key()
    {
        var f = await SeedReadyPackage();
        var input = await ApproveRelease(f);
        var family = await SeedPlaytestFamily(f.Owner);
        var building = (Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        await SeedPublishPaidEntitlement(f.Owner, building);
        await WithReleaseRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new Fire3D.Infrastructure.Releases.ReleaseWriteStore(db, TimeProvider.System);
            var built = await store.BuildAsync(f.Owner, null, input, default, "build", family);
            Assert.True(built.IsSuccess, built.Error?.Code);
            var sql = $"SELECT publish_release_gate('Publish','{f.Owner}','{family}','{built.Value!.Id}','{{}}','publish-issue',NULL)::text";
            using var result = JsonDocument.Parse((string)(await ScalarAsync(sql))!);
            Assert.Equal("Published", result.RootElement.GetProperty("result").GetProperty("status").GetString());
            var replay = (string)(await ScalarAsync(sql))!;
            Assert.Equal(result.RootElement.GetRawText(), JsonDocument.Parse(replay).RootElement.GetRawText());
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM audit_logs WHERE action='Publish'"));
            Assert.Equal("publish-issue", await ScalarAsync("SELECT idempotency_key FROM release_command_receipts WHERE operation='Publish'"));
            using var conflict=JsonDocument.Parse((string)(await ScalarAsync(sql.Replace("'{}'","'{\"different\":true}'")))!);
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",conflict.RootElement.GetProperty("code").GetString());
            await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{f.Owner}' AND family_id='{family}'");
            using var denied=JsonDocument.Parse((string)(await ScalarAsync(sql))!);
            Assert.Equal("UNAUTHORIZED",denied.RootElement.GetProperty("code").GetString());
        });
    }

    [PostgresFact]
    public async Task Review_issue_can_read_queue_and_rejection_after_reload()
    {
        var f = await SeedReadyPackage();
        await using var db = BuildingContext(testConnection);
        var store = new ScenarioReadinessStore(db);
        var submit = await store.ExecuteAsync("Submit", f.Owner, f.Owner, f.Version, null, new { }, "submit", default);
        Assert.True(submit.IsSuccess, submit.Error?.Code);
        var reject = await store.ExecuteAsync("Reject", adminId, adminId, f.Version, null,
            new { contentHash=submit.Value.GetProperty("contentHash").GetString(), rubricHash=submit.Value.GetProperty("rubricHash").GetString(), reason="Improve instructions." }, "reject", default);
        Assert.True(reject.IsSuccess, reject.Error?.Code);
        using var queue = JsonDocument.Parse((string)(await ScalarAsync($"SELECT scenario_review_read_gate('List','{adminId}','{adminId}',NULL,'{{\"status\":\"Rejected\",\"page\":1,\"pageSize\":20}}',NULL,NULL)::text"))!);
        Assert.Equal(1, queue.RootElement.GetProperty("result").GetProperty("totalCount").GetInt32());
        using var detail = JsonDocument.Parse((string)(await ScalarAsync($"SELECT scenario_review_read_gate('Version','{f.Owner}','{f.Owner}','{f.Version}','{{}}',NULL,NULL)::text"))!);
        Assert.Equal("Improve instructions.", detail.RootElement.GetProperty("result").GetProperty("review").GetProperty("reason").GetString());
        Assert.Equal("Rejected", detail.RootElement.GetProperty("result").GetProperty("review").GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task Review_reads_enforce_runtime_grants_tenant_session_filters_and_paging()
    {
        var a=await SeedReadyPackage(); var b=await SeedReadyPackage();
        await using var db=BuildingContext(testConnection);var readiness=new ScenarioReadinessStore(db);
        foreach(var f in new[]{a,b}) Assert.True((await readiness.ExecuteAsync("Submit",f.Owner,f.Owner,f.Version,null,new{},"submit",default)).IsSuccess);
        var login="review_read_"+Guid.NewGuid().ToString("N");
        var credentials=new NpgsqlConnectionStringBuilder(testConnection);
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{(credentials.Password??"").Replace("'","''")}'; GRANT USAGE ON SCHEMA public TO {login}; GRANT EXECUTE ON FUNCTION scenario_review_read_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO {login}");
        try
        {
            await using var runtime=BuildingContext(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Pooling=false}.ConnectionString);
            var queries=new ScenarioReviewQueries(runtime);
            var first=await queries.ListAsync(adminId,adminId,"Submitted",null,1,1,default);
            var second=await queries.ListAsync(adminId,adminId,"Submitted",null,2,1,default);
            Assert.True(first.IsSuccess,first.Error?.Code);Assert.Equal(2,first.Value!.TotalCount);
            Assert.NotEqual(first.Value.Items.Single().ReviewId,second.Value!.Items.Single().ReviewId);
            var org=(Guid)(await ScalarAsync($"SELECT organization_id FROM scenario_versions WHERE id='{a.Version}'"))!;
            Assert.Single((await queries.ListAsync(adminId,adminId,null,org,1,20,default)).Value!.Items);
            Assert.Equal("FORBIDDEN",(await queries.ListAsync(a.Owner,a.Owner,null,null,1,20,default)).Error?.Code);
            Assert.Equal("NOT_FOUND",(await queries.DetailAsync(a.Owner,a.Owner,b.Version,true,default)).Error?.Code);
            Assert.Equal("UNAUTHORIZED",(await queries.DetailAsync(a.Owner,Guid.NewGuid(),a.Version,true,default)).Error?.Code);
            Assert.True((await queries.DetailAsync(a.Owner,a.Owner,a.Version,true,default)).IsSuccess);
            var states=await queries.StatesAsync(a.Owner,a.Owner,[a.Version,b.Version],default);
            Assert.Single(states.Value!);Assert.Equal(a.Version,states.Value![0].ScenarioVersionId);
            Assert.Equal("VALIDATION_ERROR",(await queries.ListAsync(adminId,adminId,"42",null,1,20,default)).Error?.Code);
            Assert.Equal("VALIDATION_ERROR",(await queries.ListAsync(adminId,adminId,null,null,0,101,default)).Error?.Code);
            await runtime.Database.OpenConnectionAsync();
            await using var raw=new NpgsqlCommand("SELECT * FROM scenario_content_reviews",(NpgsqlConnection)runtime.Database.GetDbConnection());
            var permission=await Assert.ThrowsAsync<PostgresException>(()=>raw.ExecuteScalarAsync());Assert.Equal("42501",permission.SqlState);
        }
        finally {await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}");}
    }

    [PostgresFact]
    public async Task Review_HTTP_queue_detail_and_version_summary_support_reload_and_isolate_tenants()
    {
        var a=await SeedReadyPackage();var b=await SeedReadyPackage();
        await using var db=BuildingContext(testConnection);var readiness=new ScenarioReadinessStore(db);
        var submitted=await readiness.ExecuteAsync("Submit",a.Owner,a.Owner,a.Version,null,new{},"submit",default);
        var id=submitted.Value.GetProperty("reviewId").GetGuid();
        await readiness.ExecuteAsync("Reject",adminId,adminId,a.Version,null,new{contentHash=submitted.Value.GetProperty("contentHash").GetString(),rubricHash=submitted.Value.GetProperty("rubricHash").GetString(),reason="Explain the route."},"reject",default);
        var admin=await LoginAsync("admin@example.test");client.DefaultRequestHeaders.Authorization=new("Bearer",admin.AccessToken);
        var list=await client.GetAsync("/api/admin/scenario-reviews?status=Rejected&pageSize=1");Assert.Equal(HttpStatusCode.OK,list.StatusCode);
        var detail=await client.GetFromJsonAsync<ScenarioReviewDetail>($"/api/admin/scenario-reviews/{id}",Json);
        Assert.Equal("Explain the route.",detail!.Review.Reason);Assert.NotNull(detail.Rubric);
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id IN('{a.Owner}','{b.Owner}')");
        var email=(string)(await ScalarAsync($"SELECT email FROM users WHERE id='{a.Owner}'"))!;
        var owner=await LoginAsync(email);client.DefaultRequestHeaders.Authorization=new("Bearer",owner.AccessToken);
        var version=await client.GetFromJsonAsync<ScenarioVersionDetailResponse>($"/api/scenario-versions/{a.Version}",Json);
        Assert.Equal("Rejected",version!.ReviewStatus);Assert.Equal(id,version.ReviewId);Assert.Equal("Explain the route.",version.RejectReason);
        var scenario=(Guid)(await ScalarAsync($"SELECT scenario_id FROM scenario_versions WHERE id='{a.Version}'"))!;
        var versions=await client.GetFromJsonAsync<Fire3D.Application.Administration.PageResponse<ScenarioVersionSummaryResponse>>($"/api/scenarios/{scenario}/versions",Json);
        Assert.Equal("Rejected",versions!.Items.Single().ReviewStatus);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/admin/scenario-reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/scenario-versions/{b.Version}/review")).StatusCode);
    }

    [PostgresFact]
    public async Task Publish_HTTP_has_required_key_published_DTO_and_readback()
    {
        var f=await SeedReadyPackage();var request=await ApproveRelease(f);
        await ExecuteAsync($"UPDATE users SET password_hash=(SELECT password_hash FROM users WHERE id='{adminId}') WHERE id='{f.Owner}'");
        var email=(string)(await ScalarAsync($"SELECT email FROM users WHERE id='{f.Owner}'"))!;var tokens=await LoginAsync(email);
        var family=Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken).Claims.Single(c=>c.Type=="sid").Value);
        // Explicit supported runtime fixture; login does not create catalog rows.
        await ExecuteAsync("INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities) VALUES(gen_random_uuid(),'1.0.0','1','1','[]') ON CONFLICT DO NOTHING");
        await using var db=BuildingContext(testConnection);var store=new Fire3D.Infrastructure.Releases.ReleaseWriteStore(db,TimeProvider.System);
        var built=await store.BuildAsync(f.Owner,null,request,default,"build",family);Assert.True(built.IsSuccess,built.Error?.Code);
        var building=built.Value!.BuildingId;await SeedPublishPaidEntitlement(f.Owner,building);
        using var enabled=factory!.WithWebHostBuilder(web=>web.ConfigureServices(services=>services.Configure<Fire3D.Application.Releases.PublishingOptions>(options=>options.Enabled=true)));
        using var http=enabled.CreateClient();http.DefaultRequestHeaders.Authorization=new("Bearer",tokens.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsync($"/api/releases/{built.Value.Id}/publish",null)).StatusCode);
        http.DefaultRequestHeaders.Add("Idempotency-Key","publish-http");
        var response=await http.PostAsync($"/api/releases/{built.Value.Id}/publish",null);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var value=await response.Content.ReadFromJsonAsync<Fire3D.Application.Releases.ReleaseResponse>(Json);Assert.Equal("Published",value!.Status);
        var replay=await http.PostAsync($"/api/releases/{built.Value.Id}/publish",null);Assert.Equal(await response.Content.ReadAsStringAsync(),await replay.Content.ReadAsStringAsync());
        var get=await http.GetFromJsonAsync<Fire3D.Application.Releases.ReleaseResponse>($"/api/releases/{built.Value.Id}",Json);Assert.Equal("Published",get!.Status);
        var trainee=Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO users(id,email,username,role,is_active,created_at,updated_at) VALUES('{trainee}','learner-{trainee:N}@example.test','learner_'||left(md5(gen_random_uuid()::text),20),'Trainee',true,now(),now()); UPDATE buildings SET visibility='Public' WHERE id='{building}'");
        var tf=await SeedPlaytestFamily(trainee);
        var trainings=new Fire3D.Infrastructure.Buildings.TrainingReadStore(db);
        Assert.Single((await trainings.GetTrainingsByBuildingAsync(trainee,building,null,default,tf)).Value!);
        Assert.True((await store.RevokeAsync(f.Owner,built.Value.Id,null,"Withdraw publication",default,family)).IsSuccess);
        Assert.Empty((await trainings.GetTrainingsByBuildingAsync(trainee,building,null,default,tf)).Value!);
    }

    [PostgresFact]
    public async Task Publish_failure_codes_distinguish_training_approval_QA_artifact_and_runtime()
    {
        var f=await SeedReadyPackage();var request=await ApproveRelease(f);var family=await SeedPlaytestFamily(f.Owner);
        await using var db=BuildingContext(testConnection);
        var store=new Fire3D.Infrastructure.Releases.ReleaseWriteStore(db,TimeProvider.System,Microsoft.Extensions.Options.Options.Create(new Fire3D.Application.Releases.PublishingOptions{Enabled=true}));
        var build=await store.BuildAsync(f.Owner,null,request,default,"build",family);Assert.True(build.IsSuccess,build.Error?.Code);
        await SeedPublishPaidEntitlement(f.Owner,build.Value!.BuildingId);
        async Task Check(string setup,string code,string restore)
        {
            await ExecuteAsync(setup);
            try{Assert.Equal(code,(await store.PublishAsync(f.Owner,build.Value.Id,null,default,family,"publish")).Error?.Code);}
            finally{await ExecuteAsync(restore);}
            Assert.Equal("Built",await ScalarAsync($"SELECT status::text FROM releases WHERE id='{build.Value.Id}'"));
            Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM release_command_receipts WHERE operation='Publish'"));
        }
        await Check($"UPDATE trainings SET status='Archived' WHERE release_id='{build.Value.Id}'","TRAINING_INACTIVE",$"UPDATE trainings SET status='Active' WHERE release_id='{build.Value.Id}'");
        await Check($"ALTER TABLE scenario_content_reviews DISABLE TRIGGER USER;UPDATE scenario_content_reviews SET content_hash=repeat('0',64) WHERE scenario_version_id='{f.Version}';ALTER TABLE scenario_content_reviews ENABLE TRIGGER USER",
            "CONTENT_APPROVAL_REQUIRED",$"ALTER TABLE scenario_content_reviews DISABLE TRIGGER USER;UPDATE scenario_content_reviews SET content_hash=(SELECT scenario_hash FROM scenario_versions WHERE id='{f.Version}') WHERE scenario_version_id='{f.Version}';ALTER TABLE scenario_content_reviews ENABLE TRIGGER USER");
        await Check($"INSERT INTO revision_issues(id,validation_run_id,severity,code,message,details,created_at) VALUES(gen_random_uuid(),'{f.Run}','Error','fixture-blocker','Fixture blocker','{{}}',now())",
            "RELEASE_QA_BLOCKERS_PRESENT",$"DELETE FROM revision_issues WHERE validation_run_id='{f.Run}' AND code='fixture-blocker'");
        await Check($"UPDATE validation_runs SET outcome='Failed' WHERE id='{f.Run}'","RELEASE_QA_NOT_PASSED",$"UPDATE validation_runs SET outcome='Passed' WHERE id='{f.Run}'");
        await Check($"UPDATE revision_artifacts SET is_runtime_ready=false WHERE id='{f.Artifact}'","RELEASE_ARTIFACT_REQUIRED",$"UPDATE revision_artifacts SET is_runtime_ready=true WHERE id='{f.Artifact}'");
        await Check($"ALTER TABLE release_packages DISABLE TRIGGER USER;UPDATE release_packages SET build_target='InvalidTarget' WHERE release_id='{build.Value.Id}';ALTER TABLE release_packages ENABLE TRIGGER USER",
            "PACKAGE_COMPATIBILITY_REQUIRED",$"ALTER TABLE release_packages DISABLE TRIGGER USER;UPDATE release_packages SET build_target='Windows' WHERE release_id='{build.Value.Id}';ALTER TABLE release_packages ENABLE TRIGGER USER");
    }

    [PostgresFact]
    public async Task Publish_review_OpenAPI_describes_headers_response_and_roles()
    {
        var json=await client.GetStringAsync("/openapi/v1.json");
        await File.WriteAllTextAsync(Path.Combine(FindRepoRoot(),".codex/local/openapi.json"),json);
        using var doc=JsonDocument.Parse(json);var paths=doc.RootElement.GetProperty("paths");
        var publish=paths.GetProperty("/api/releases/{releaseId}/publish").GetProperty("post");
        Assert.True(publish.GetProperty("responses").TryGetProperty("200",out _));
        Assert.False(publish.GetProperty("responses").TryGetProperty("204",out _));
        Assert.Contains(publish.GetProperty("parameters").EnumerateArray(),p=>p.GetProperty("name").GetString()=="Idempotency-Key" && p.GetProperty("required").GetBoolean());
        foreach(var route in new[]{"/api/admin/scenario-reviews","/api/admin/scenario-reviews/{reviewId}","/api/scenario-versions/{versionId}/review"})
        {
            var operation=paths.GetProperty(route).GetProperty("get");
            Assert.True(operation.GetProperty("responses").TryGetProperty("200",out _));
            Assert.NotEmpty(operation.GetProperty("security").EnumerateArray());
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(route.Replace("{reviewId}",Guid.NewGuid().ToString()).Replace("{versionId}",Guid.NewGuid().ToString()))).StatusCode);
        }
    }
}
