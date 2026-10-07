using System.Net;
using System.Net.Http.Json;
using Xunit;
using Npgsql;
using Fire3D.Infrastructure.Scenarios;
using Fire3D.Application.Scenarios;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Content_review_routes_require_bearer_before_any_mutation()
    {
        foreach(var path in new[]{"/api/scenario-versions/"+Guid.NewGuid()+"/submit","/api/admin/scenario-versions/"+Guid.NewGuid()+"/approve","/api/admin/scenario-versions/"+Guid.NewGuid()+"/reject"})
        {
            var result=await client.PostAsJsonAsync(path,new{});Assert.Equal(HttpStatusCode.Unauthorized,result.StatusCode);
        }
    }    private async Task<(Guid Owner,Guid Revision,Guid Version,Guid Run,Guid Artifact)> SeedReadyPackage(string kind="ReleasePackage",bool blocker=false,string minRuntimeVersion="1.0.0",string[]? requiredCapabilities=null,string manifestProtocolVersion="1")
    {
        var revision=await SeedVerifiedIfcRevision();var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{revision}'"))!;var org=(Guid)(await ScalarAsync($"SELECT organization_id FROM revisions WHERE id='{revision}'"))!;var owner=Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO users(id,organization_id,email,full_name,role,is_active,email_verified_at,created_at,updated_at) VALUES('{owner}','{org}','owner-{owner:N}@example.test','Owner','OrganizationUser',true,now(),now(),now())");
        await using var authoringDb=BuildingContext(testConnection);var authoring=new ScenarioWriteStore(authoringDb);
        var scenario=(await authoring.CreateScenarioAsync(owner,building,org,new(building,"Ready package"),default,"ready-scenario")).Value;
        var draft=(await authoring.CreateScenarioDraftAsync(owner,scenario,org,new(revision),default,"ready-draft")).Value;
        var xmin=Convert.ToUInt32(await ScalarAsync($"SELECT xmin::text::bigint FROM scenario_drafts WHERE id='{draft}'"));
        var updated=await authoring.UpdateScenarioDraftAsync(owner,draft,xmin,ValidAuthoringState(),org,default);Assert.True(updated.IsSuccess,updated.Error?.Code);
        var snapshot=await authoring.SnapshotScenarioDraftAsync(owner,draft,org,default,"ready-snapshot",updated.Value);Assert.True(snapshot.IsSuccess,snapshot.Error?.Code);var version=snapshot.Value;
        var build=await authoring.BuildAsync(owner,version,new(kind,"Windows"),"ready-build",default);Assert.True(build.IsSuccess,build.Error?.Code);var job=build.Value;Guid run=default,artifact=default;
        await WithProcessingRuntime(async(api,worker)=>
        {
            await using var db=BuildingContext(api);var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:ProcessingExecutor"]=worker}).Build();var store=new Fire3D.Infrastructure.Ifc.ProcessingRuntimeStore(db,config);
            var delivery=await store.ExecuteAsync("Claim",null,null,null,default);var hash=(string)(await ScalarAsync($"SELECT input_hash FROM processing_jobs WHERE id='{job}'"))!;
            var claim=await store.ExecuteAsync("Claim",job,WorkerInput(new{eventKey=delivery.GetProperty("eventKey").GetString(),payloadHash=delivery.GetProperty("payloadHash").GetString(),inputHash=hash,toolchainVersion="fake-unity-v1"}),default);Assert.True(claim.IsSuccess,claim.Error?.Code);
            var attempt=claim.Value.GetProperty("attemptId").GetGuid();var lease=claim.Value.GetProperty("leaseToken").GetGuid();string? outputHash=null;
            foreach(var type in new[]{"unity_package","manifest"})
            {
                var issues=blocker?new object[]{new{severity="Critical",code="BLOCKER",message="Fixture blocker",details=new{}}}:Array.Empty<object>();
                var output=new{artifactType=type,objectKey=claim.Value.GetProperty("outputPrefix").GetString()+type,sha256Hash=IfcHash,sizeBytes=100L,schemaVersion="1",metadata=new{buildTarget="Windows",minRuntimeVersion,protocolVersion=type=="manifest"?manifestProtocolVersion:"1",manifestSchemaVersion="1",requiredCapabilities=requiredCapabilities??Array.Empty<string>()},validatorVersion="fake-validator-v1",outcome="Passed",issues};
                var registered=await store.ExecuteAsync("Output",job,WorkerInput(new{attemptId=attempt,leaseToken=lease,output}),default);Assert.True(registered.IsSuccess,registered.Error?.Code);outputHash=registered.Value.GetProperty("outputHash").GetString();if(type=="unity_package")artifact=registered.Value.GetProperty("artifactId").GetGuid();
            }
            var completed=await store.ExecuteAsync("Complete",job,WorkerInput(new{attemptId=attempt,leaseToken=lease,outputHash}),default);Assert.True(completed.IsSuccess,completed.Error?.Code);run=completed.Value.GetProperty("validationRunId").GetGuid();
        });
        return(owner,revision,version,run,artifact);
    }
    private async Task WithReadinessRuntime(Func<string,Task> action)
    {
        var login="readiness_test_"+Guid.NewGuid().ToString("N");await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS;GRANT USAGE ON SCHEMA public TO {login};GRANT EXECUTE ON FUNCTION scenario_readiness_gate(text,uuid,uuid,uuid,jsonb,text) TO {login}");
        try{await action(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Password="",Pooling=false}.ConnectionString);}
        finally{await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}");}
    }
    [PostgresFact]
    public async Task Readiness_exact_pair_and_content_decisions_are_scoped_atomic_and_replay_safe()
    {
        var fixture=await SeedReadyPackage();
        await WithReadinessRuntime(async runtime=>
        {
            await using var db=BuildingContext(runtime);await using var otherDb=BuildingContext(runtime);var store=new ScenarioReadinessStore(db);var other=new ScenarioReadinessStore(otherDb);
            var confirm=new ConfirmTrainingRequest(fixture.Version,fixture.Run);
            var requests=await Task.WhenAll(store.ExecuteAsync("Confirm",fixture.Owner,fixture.Version,fixture.Revision,confirm,null,default),other.ExecuteAsync("Confirm",fixture.Owner,fixture.Version,fixture.Revision,confirm,null,default));
            Assert.All(requests,x=>Assert.True(x.IsSuccess,x.Error?.Code));Assert.Equal(requests[0].Value.GetProperty("reviewId").GetGuid(),requests[1].Value.GetProperty("reviewId").GetGuid());
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM revision_reviews WHERE scenario_version_id='{fixture.Version}' AND action='ConfirmForTraining'"));
            Assert.Equal("READINESS_PROVENANCE_MISMATCH",(await store.ExecuteAsync("Confirm",fixture.Owner,fixture.Version,fixture.Revision,confirm with{ValidationRunId=Guid.NewGuid()},null,default)).Error?.Code);
            Assert.Equal("READINESS_PROVENANCE_MISMATCH",(await store.ExecuteAsync("Confirm",fixture.Owner,fixture.Version,fixture.Revision,confirm with{AnnotationSetId=Guid.NewGuid()},null,default)).Error?.Code);
            Assert.Equal("FORBIDDEN",(await store.ExecuteAsync("Submit",adminId,fixture.Version,null,new{},"admin-submit",default)).Error?.Code);
            var submitted=await store.ExecuteAsync("Submit",fixture.Owner,fixture.Version,null,new{},"submit-one",default);Assert.True(submitted.IsSuccess,submitted.Error?.Code);
            var hashes=new ContentReviewDecisionRequest(submitted.Value.GetProperty("contentHash").GetString()!,submitted.Value.GetProperty("rubricHash").GetString()!);
            Assert.Equal("FORBIDDEN",(await store.ExecuteAsync("Approve",fixture.Owner,fixture.Version,null,hashes,"owner-approve",default)).Error?.Code);
            Assert.Equal("CONTENT_HASH_MISMATCH",(await store.ExecuteAsync("Approve",adminId,fixture.Version,null,hashes with{ContentHash=new string('b',64)},"wrong-hash",default)).Error?.Code);
            await ExecuteAsync($"ALTER TABLE audit_logs ADD CONSTRAINT review_audit_fault CHECK(target_id<>'{fixture.Version}'::uuid) NOT VALID");
            await Assert.ThrowsAsync<PostgresException>(()=>store.ExecuteAsync("Approve",adminId,fixture.Version,null,hashes,"approve-one",default));
            Assert.Equal("Submitted",await ScalarAsync($"SELECT status FROM scenario_content_reviews WHERE scenario_version_id='{fixture.Version}'"));Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM readiness_command_receipts WHERE idempotency_key='approve-one'"));
            await ExecuteAsync("ALTER TABLE audit_logs DROP CONSTRAINT review_audit_fault");
            var approved=await store.ExecuteAsync("Approve",adminId,fixture.Version,null,hashes,"approve-one",default);Assert.True(approved.IsSuccess,approved.Error?.Code);Assert.Equal("Approved",approved.Value.GetProperty("status").GetString());
            var before=await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{fixture.Version}'");
            Assert.True((await store.ExecuteAsync("Approve",adminId,fixture.Version,null,hashes,"approve-one",default)).IsSuccess);Assert.Equal(before,await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{fixture.Version}'"));
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT",(await store.ExecuteAsync("Approve",adminId,fixture.Version,null,hashes with{Reason="Different"},"approve-one",default)).Error?.Code);
            Assert.Equal("CONTENT_REVIEW_NOT_PENDING",(await store.ExecuteAsync("Reject",adminId,fixture.Version,null,hashes with{Reason="Late reject"},"late-reject",default)).Error?.Code);
            await Assert.ThrowsAsync<PostgresException>(()=>ExecuteAsync($"UPDATE scenario_content_reviews SET status='Rejected',reason='tamper' WHERE scenario_version_id='{fixture.Version}'"));
            var technical=await store.ExecuteAsync("TechnicalReject",fixture.Owner,fixture.Version,fixture.Revision,new{scenarioVersionId=fixture.Version,validationRunId=fixture.Run,annotationSetId=(Guid?)null,reviewMessage="Technical concern"},null,default);Assert.True(technical.IsSuccess,technical.Error?.Code);
            Assert.Equal("ConfirmedForTraining",await ScalarAsync($"SELECT status::text FROM revisions WHERE id='{fixture.Revision}'"));
        });
    }
    [PostgresFact]
    public async Task Readiness_blockers_and_lifecycle_are_checked_before_confirmation()
    {
        var fixture=await SeedReadyPackage(blocker:true);await using var db=BuildingContext(testConnection);var store=new ScenarioReadinessStore(db);var input=new ConfirmTrainingRequest(fixture.Version,fixture.Run);
        Assert.Equal("READINESS_BLOCKED",(await store.ExecuteAsync("Confirm",fixture.Owner,fixture.Version,fixture.Revision,input,null,default)).Error?.Code);
        await ExecuteAsync($"UPDATE users SET is_active=false WHERE id='{fixture.Owner}'");
        Assert.Equal("UNAUTHORIZED",(await store.ExecuteAsync("Confirm",fixture.Owner,fixture.Version,fixture.Revision,input,null,default)).Error?.Code);
        Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM revision_reviews WHERE scenario_version_id='{fixture.Version}'"));
    }

    [PostgresFact]
    public async Task Ifc_dependency_grants_work_for_non_superuser_without_retaining_owner_membership()
    {
        var login="grant_repair_"+Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN CREATEROLE NOSUPERUSER NOBYPASSRLS; GRANT USAGE ON SCHEMA public TO {login}; GRANT fet3d_integration_owner TO {login} WITH ADMIN TRUE, SET FALSE, INHERIT FALSE; REVOKE EXECUTE ON FUNCTION fet3d_jsonb_payload_hash(jsonb),enqueue_integration_outbox_event(text,text,uuid,text,text,jsonb),enqueue_integration_outbox_event_internal(text,text,uuid,text,text,jsonb,boolean,boolean) FROM fet3d_ifc_upload_owner");
        try
        {
            using var resource=typeof(ScenarioReadinessStore).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.IfcDependencyGrants.sql")!;
            var sql=await new StreamReader(resource).ReadToEndAsync();
            await using var connection=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(testConnection){Username=login,Password="",Pooling=false}.ConnectionString);await connection.OpenAsync();
            await using var transaction=await connection.BeginTransactionAsync();await using var command=new NpgsqlCommand(sql,connection,transaction);await command.ExecuteNonQueryAsync();await transaction.CommitAsync();
            Assert.Equal(false,await ScalarAsync($"SELECT pg_has_role('{login}','fet3d_integration_owner','SET')"));
            Assert.Equal(true,await ScalarAsync("SELECT has_function_privilege('fet3d_ifc_upload_owner','enqueue_integration_outbox_event(text,text,uuid,text,text,jsonb)','EXECUTE')"));
            await using var query=new NpgsqlCommand("SET ROLE fet3d_ifc_upload_owner;SELECT fet3d_jsonb_payload_hash('{}'::jsonb)",connection);
            // This migration identity must not gain the IFC owner role while repairing grants.
            await Assert.ThrowsAsync<PostgresException>(()=>query.ExecuteScalarAsync());
        }
        finally{await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}");}
    }

}
