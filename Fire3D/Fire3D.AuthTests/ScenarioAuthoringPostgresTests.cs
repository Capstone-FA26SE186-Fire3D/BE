using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Scenarios.Dto;
using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Scenarios;
using Npgsql;
using Xunit;
using Microsoft.Extensions.Configuration;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
    private static ScenarioDraftStateDto ValidAuthoringState()=>new([new(1,2,3,0)],[],new(100,60,5),new(["exit"]),JsonNode.Parse("""{"schema_version":"1","pass_threshold":1,"criteria":[{"id":"exit","metric":"exit","mandatory":true,"weight":1,"operator":"gte","threshold":1}]}""")!.AsObject(),["Evacuate"],"Follow exits");
    private async Task WithAuthoringRuntime(Func<string,Task> action)
    {
        var login="authoring_test_"+Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS;GRANT USAGE ON SCHEMA public TO {login};GRANT SELECT ON users,organizations,buildings,scenario_drafts,scenario_versions,scenarios TO {login};GRANT EXECUTE ON FUNCTION scenario_authoring_gate(text,uuid,uuid,jsonb,text,bigint),scenario_reference_issues(uuid,jsonb) TO {login}");
        foreach(var table in new[]{"users","organizations","buildings","scenario_drafts","scenario_versions","scenarios"}) await ExecuteAsync($"CREATE POLICY {login} ON {table} TO {login} USING(true)");
        try{await action(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Password="",Pooling=false}.ConnectionString);}
        finally
        {
            foreach(var table in new[]{"users","organizations","buildings","scenario_drafts","scenario_versions","scenarios"}) await ExecuteAsync($"DROP POLICY {login} ON {table}");
            await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}");
        }
    }
    [PostgresFact]
    public async Task Scenario_numbering_snapshot_receipt_etag_and_immutable_input_are_atomic()
    {
        var revision=await SeedVerifiedIfcRevision();var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        await WithAuthoringRuntime(async runtime=>
        {
            await using var db=BuildingContext(runtime);await using var second=BuildingContext(runtime);var store=new ScenarioWriteStore(db);var other=new ScenarioWriteStore(second);
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED",(await store.CreateScenarioAsync(adminId,building,null,new(building,"Scenario"),default)).Error?.Code);
            var create=await store.CreateScenarioAsync(adminId,building,null,new(building,"Scenario"),default,"scenario-one");Assert.True(create.IsSuccess,create.Error?.Code);var scenario=create.Value;
            Assert.Equal(scenario,(await store.CreateScenarioAsync(adminId,building,null,new(building,"Scenario"),default,"scenario-one")).Value);
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",(await store.CreateScenarioAsync(adminId,building,null,new(building,"Other"),default,"scenario-one")).Error?.Code);
            var drafts=await Task.WhenAll(store.CreateScenarioDraftAsync(adminId,scenario,null,new(revision),default,"draft-a"),other.CreateScenarioDraftAsync(adminId,scenario,null,new(revision),default,"draft-b"));
            Assert.All(drafts,x=>Assert.True(x.IsSuccess,x.Error?.Code));Assert.Equal(2L,await ScalarAsync($"SELECT count(DISTINCT draft_number) FROM scenario_drafts WHERE scenario_id='{scenario}'"));
            var draft=drafts[0].Value;var initial=Convert.ToUInt32(await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft}'"));
            var update=await store.UpdateScenarioDraftAsync(adminId,draft,initial,ValidAuthoringState(),null,default);Assert.True(update.IsSuccess,update.Error?.Code);
            Assert.Equal(412,(await other.UpdateScenarioDraftAsync(adminId,draft,initial,ValidAuthoringState(),null,default)).Error?.Status);
            Assert.Equal(428,(await store.SnapshotScenarioDraftAsync(adminId,draft,null,default,"missing")).Error?.Status);
            var snapshots=await Task.WhenAll(store.SnapshotScenarioDraftAsync(adminId,draft,null,default,"snapshot-one",update.Value),other.SnapshotScenarioDraftAsync(adminId,draft,null,default,"snapshot-one",update.Value));
            Assert.All(snapshots,x=>Assert.True(x.IsSuccess,x.Error?.Code));Assert.Equal(snapshots[0].Value,snapshots[1].Value);var version=snapshots[0].Value;
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM scenario_versions WHERE scenario_id='{scenario}' AND state_snapshot IS NOT NULL AND scenario_hash=fet3d_jsonb_payload_hash(state_snapshot) AND rubric IS NOT NULL"));
            await Assert.ThrowsAsync<PostgresException>(()=>ExecuteAsync($"UPDATE scenario_versions SET name='tamper' WHERE id='{version}'"));
            var build=await store.BuildAsync(adminId,version,new("PlaytestPackage","Windows"),"build-one",default);Assert.True(build.IsSuccess,build.Error?.Code);
            Assert.Equal(build.Value,(await other.BuildAsync(adminId,version,new("PlaytestPackage","Windows"),"build-two",default)).Value);
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM processing_jobs WHERE scenario_version_id='{version}' AND input_hash=fet3d_jsonb_payload_hash(input_snapshot)"));
            await Assert.ThrowsAsync<PostgresException>(()=>ExecuteAsync($"UPDATE processing_jobs SET input_snapshot='{{}}' WHERE id='{build.Value}'"));
            var detail=await new ScenarioReadStore(db).GetScenarioVersionAsync(version,null,default);Assert.NotNull(detail?.Rubric);Assert.Equal("Follow exits",detail!.LearnerInstructions);
            await ExecuteAsync($"ALTER TABLE audit_logs ADD CONSTRAINT authoring_audit_fault CHECK(target_id<>'{draft}'::uuid) NOT VALID");
            var before=await ScalarAsync($"SELECT state::text FROM scenario_drafts WHERE id='{draft}'");
            await Assert.ThrowsAsync<PostgresException>(()=>store.UpdateScenarioDraftAsync(adminId,draft,update.Value,ValidAuthoringState() with{LearnerInstructions="Changed"},null,default));
            Assert.Equal(before,await ScalarAsync($"SELECT state::text FROM scenario_drafts WHERE id='{draft}'"));
            Assert.Equal((long)update.Value,await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft}'"));
        });
    }
    [PostgresFact]
    public async Task Scenario_http_headers_validation_and_audit_failure_do_not_create_receipts()
    {
        var revision=await SeedVerifiedIfcRevision();var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
        var create=await client.PostAsJsonAsync("/api/scenarios",new{buildingId=building,name="Draft test"});Assert.Equal(HttpStatusCode.BadRequest,create.StatusCode);
        client.DefaultRequestHeaders.Add("Idempotency-Key","http-scenario");create=await client.PostAsJsonAsync("/api/scenarios",new{buildingId=building,name="Draft test"});Assert.Equal(HttpStatusCode.Created,create.StatusCode);
        var scenario=(await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Remove("Idempotency-Key");client.DefaultRequestHeaders.Add("Idempotency-Key","http-draft");
        var createdDraft=await client.PostAsJsonAsync($"/api/scenarios/{scenario}/draft",new{revisionId=revision});Assert.Equal(HttpStatusCode.Created,createdDraft.StatusCode);var draft=(await createdDraft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var detail=await client.GetAsync($"/api/scenario-drafts/{draft}");var etag=detail.Headers.ETag!.ToString();
        Assert.Equal((HttpStatusCode)428,(await client.PutAsJsonAsync($"/api/scenario-drafts/{draft}",ValidAuthoringState())).StatusCode);
        client.DefaultRequestHeaders.Add("If-Match","\"abc\"");Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync($"/api/scenario-drafts/{draft}",ValidAuthoringState())).StatusCode);
        client.DefaultRequestHeaders.Remove("If-Match");client.DefaultRequestHeaders.Add("If-Match",etag);
        var put=await client.PutAsJsonAsync($"/api/scenario-drafts/{draft}",ValidAuthoringState() with{ObjectAnchors=["missing-anchor"]});Assert.Equal(HttpStatusCode.NoContent,put.StatusCode);
        client.DefaultRequestHeaders.Remove("If-Match");client.DefaultRequestHeaders.Add("If-Match",put.Headers.ETag!.ToString());
        client.DefaultRequestHeaders.Remove("Idempotency-Key");client.DefaultRequestHeaders.Add("Idempotency-Key","http-snapshot");
        var validation=await client.PostAsync($"/api/scenario-drafts/{draft}/validate",null);Assert.Equal(HttpStatusCode.OK,validation.StatusCode);Assert.Contains("ANCHOR_NOT_FOUND",await validation.Content.ReadAsStringAsync());
        var snapshot=await client.PostAsync($"/api/scenario-drafts/{draft}/snapshot",null);Assert.Equal(HttpStatusCode.BadRequest,snapshot.StatusCode);
        Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM authoring_command_receipts WHERE operation='Snapshot' AND idempotency_key='http-snapshot'"));
        await ExecuteAsync("ALTER TABLE audit_logs ADD CONSTRAINT authoring_create_fault CHECK(target_entity<>'Scenario') NOT VALID");
        await using var db=BuildingContext(testConnection);await Assert.ThrowsAsync<PostgresException>(()=>new ScenarioWriteStore(db).CreateScenarioAsync(adminId,building,null,new(building,"Rollback"),default,"rollback-create"));
        Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM authoring_command_receipts WHERE idempotency_key='rollback-create'"));
        Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM scenarios WHERE name='Rollback'"));
    }    [PostgresFact]
    public async Task Scenario_package_worker_claim_pins_version_and_accepts_server_bound_outputs()
    {
        var revision=await SeedVerifiedIfcRevision();var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;
        await using var authoringDb=BuildingContext(testConnection);var authoring=new ScenarioWriteStore(authoringDb);
        var scenario=(await authoring.CreateScenarioAsync(adminId,building,null,new(building,"Package"),default,"package-scenario")).Value;
        var draft=(await authoring.CreateScenarioDraftAsync(adminId,scenario,null,new(revision),default,"package-draft")).Value;
        var xmin=Convert.ToUInt32(await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft}'"));
        var updated=await authoring.UpdateScenarioDraftAsync(adminId,draft,xmin,ValidAuthoringState(),null,default);
        var version=(await authoring.SnapshotScenarioDraftAsync(adminId,draft,null,default,"package-snapshot",updated.Value)).Value;
        var job=(await authoring.BuildAsync(adminId,version,new("PlaytestPackage","Windows"),"package-build",default)).Value;
        await WithProcessingRuntime(async(api,worker)=>
        {
            await using var db=BuildingContext(api);var config=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:ProcessingExecutor"]=worker}).Build();
            var store=new Fire3D.Infrastructure.Ifc.ProcessingRuntimeStore(db,config);
            var delivery=await store.ExecuteAsync("Claim",null,null,null,default);
            var hash=(string)(await ScalarAsync($"SELECT input_hash FROM processing_jobs WHERE id='{job}'"))!;
            var claim=await store.ExecuteAsync("Claim",job,WorkerInput(new{eventKey=delivery.GetProperty("eventKey").GetString(),payloadHash=delivery.GetProperty("payloadHash").GetString(),inputHash=hash,toolchainVersion="fake-unity-v1"}),default);
            Assert.True(claim.IsSuccess,claim.Error?.Code);Assert.Equal(version,claim.Value.GetProperty("inputSnapshot").GetProperty("scenarioVersionId").GetGuid());Assert.Equal("Follow exits",claim.Value.GetProperty("scenarioSnapshot").GetProperty("learnerInstructions").GetString());
            var attempt=claim.Value.GetProperty("attemptId").GetGuid();var lease=claim.Value.GetProperty("leaseToken").GetGuid();string? outputHash=null;
            foreach(var type in new[]{"unity_package","manifest"})
            {
                var output=new{artifactType=type,objectKey=claim.Value.GetProperty("outputPrefix").GetString()+type,sha256Hash=IfcHash,sizeBytes=100L,schemaVersion="1",metadata=new{buildTarget="Windows",minRuntimeVersion="1.0.0",protocolVersion="1",manifestSchemaVersion="1",requiredCapabilities=Array.Empty<string>()},validatorVersion="fake-validator-v1",outcome="Passed",issues=Array.Empty<object>()};
                var registered=await store.ExecuteAsync("Output",job,WorkerInput(new{attemptId=attempt,leaseToken=lease,output}),default);Assert.True(registered.IsSuccess,registered.Error?.Code);outputHash=registered.Value.GetProperty("outputHash").GetString();
            }
            Assert.True((await store.ExecuteAsync("Complete",job,WorkerInput(new{attemptId=attempt,leaseToken=lease,outputHash}),default)).IsSuccess);
            Assert.Equal(2L,await ScalarAsync($"SELECT count(*) FROM revision_artifacts WHERE job_id='{job}' AND attempt_id='{attempt}' AND is_runtime_ready"));
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM validation_runs v JOIN scenario_versions s ON s.id=v.scenario_version_id WHERE v.job_id='{job}' AND v.scenario_hash=s.scenario_hash AND outcome='Passed'"));
        });
    }

}
