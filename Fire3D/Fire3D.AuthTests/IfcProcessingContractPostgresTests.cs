using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using Xunit;
namespace Fire3D.AuthTests;
public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Ifc_process_requires_a_key_and_does_not_trust_legacy_source_metadata()
    {
        var building=await SeedIfcBuilding(); var revision=Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO revisions(id,building_id,organization_id,uploaded_by,version_label,primary_type,status,created_at,updated_at) SELECT '{revision}',id,organization_id,'{adminId}','legacy','IFC','Uploaded',now(),now() FROM buildings WHERE id='{building}'; INSERT INTO source_documents(id,revision_id,uploaded_by,original_filename,file_size_bytes,storage_url,mime_type,sha256_hash,usage_rights,file_type,quarantine_status,created_at) VALUES(gen_random_uuid(),'{revision}','{adminId}','legacy.ifc',10,'legacy/unverified.ifc','application/octet-stream',repeat('a',64),'Private','IFC','Pending',now())");
        client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
        var missing=await client.PostAsync($"/api/revisions/{revision}/process",null);
        Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);
        client.DefaultRequestHeaders.Add("Idempotency-Key","legacy-source");
        var legacy=await client.PostAsync($"/api/revisions/{revision}/process",null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,legacy.StatusCode);
        Assert.Contains("IFC_SOURCE_NOT_VERIFIED",await legacy.Content.ReadAsStringAsync());
        Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM processing_jobs WHERE revision_id='{revision}'"));
        Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM source_documents WHERE revision_id='{revision}' AND upload_verified_at IS NOT NULL"));
    }
    private static readonly JsonSerializerOptions WorkerJson=new(JsonSerializerDefaults.Web);
    private static JsonElement WorkerInput(object value)=>JsonSerializer.SerializeToElement(value,WorkerJson);
    private async Task<Guid> SeedVerifiedIfcRevision()
    {
        var building=await SeedIfcBuilding();var fake=new IfcMemoryStorage();await using var context=BuildingContext(testConnection);
        var flow=new IfcUploadService(new IfcUploadStore(context),fake.Storage,fake.Inspector,IfcOptions,TimeProvider.System);
        var upload=(await flow.InitiateAsync(adminId,building,new(IfcBytes.Length,"model.ifc","v1",IfcHash),Guid.NewGuid().ToString("N"),default)).Value!;
        fake.Put(upload.ObjectKey,IfcBytes);Assert.True((await flow.CompleteAsync(adminId,upload.RevisionId,CompleteInput(upload),default)).IsSuccess);
        return upload.RevisionId;
    }
    [PostgresFact]
    public async Task Ifc_process_concurrency_delivery_replay_stale_fencing_and_retry_preserve_one_logical_job()
    {
        var revision=await SeedVerifiedIfcRevision();
        await WithProcessingRuntime(async(api,worker)=>
        {
            await using var db=BuildingContext(api);await using var other=BuildingContext(api);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:ProcessingExecutor"]=worker}).Build();
            var store=new ProcessingRuntimeStore(db,config);var second=new ProcessingRuntimeStore(other,config);
            var jobs=await Task.WhenAll(store.RequestAsync(adminId,revision,"process-1",default),second.RequestAsync(adminId,revision,"process-2",default));
            Assert.All(jobs,x=>Assert.True(x.IsSuccess,x.Error?.Code));Assert.Equal(jobs[0].Value,jobs[1].Value);var job=jobs[0].Value;
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM processing_jobs WHERE revision_id='{revision}'"));
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM integration_outbox_events WHERE aggregate_id='{job}' AND schema_version='1' AND payload_hash=fet3d_jsonb_payload_hash(payload) AND organization_id=(SELECT organization_id FROM revisions WHERE id='{revision}')"));
            await Assert.ThrowsAsync<PostgresException>(()=>ExecuteAsync($"UPDATE integration_outbox_events SET payload_hash=repeat('a',64) WHERE aggregate_id='{job}'"));
            var delivery=await store.ExecuteAsync("Claim",null,null,null,default);var eventKey=delivery.GetProperty("eventKey").GetString()!;
            var input=WorkerInput(new{eventKey,payloadHash=delivery.GetProperty("payloadHash").GetString(),inputHash=IfcHash,toolchainVersion="fake-ifc-v1"});
            var claims=await Task.WhenAll(store.ExecuteAsync("Claim",job,input,default),second.ExecuteAsync("Claim",job,input,default));
            Assert.All(claims,x=>Assert.True(x.IsSuccess,x.Error?.Code));var attempt=claims[0].Value.GetProperty("attemptId").GetGuid();var lease=claims[0].Value.GetProperty("leaseToken").GetGuid();
            Assert.Equal(attempt,claims[1].Value.GetProperty("attemptId").GetGuid());
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM processing_job_attempts WHERE processing_job_id='{job}'"));
            Assert.Equal("RECEIPT_REQUIRED",(await store.ExecuteAsync("Ack",eventKey,delivery.GetProperty("leaseToken").GetGuid(),Guid.NewGuid(),default)).GetProperty("code").GetString());
            // Losing worker ACK re-delivers the same event/receipt; no second claim effect.
            await store.ExecuteAsync("Fail",eventKey,delivery.GetProperty("leaseToken").GetGuid(),null,default);
            await ExecuteAsync("UPDATE integration_outbox_events SET available_at=now()-interval '1 second'");
            var replayDelivery=await store.ExecuteAsync("Claim",null,null,null,default);
            Assert.Equal(eventKey,replayDelivery.GetProperty("eventKey").GetString());
            var receipt=claims[0].Value.GetProperty("receiptId").GetGuid();
            Assert.Equal("OK",(await store.ExecuteAsync("Ack",eventKey,replayDelivery.GetProperty("leaseToken").GetGuid(),receipt,default)).GetProperty("code").GetString());
            await ExecuteAsync($"UPDATE processing_job_attempts SET lease_until=now()-interval '1 second' WHERE id='{attempt}'");
            Assert.Equal("OK",(await store.ExecuteAsync("Recover",null,null,null,default)).GetProperty("code").GetString());
            Assert.Equal("ATTEMPT_STALE",(await store.ExecuteAsync("Renew",job,WorkerInput(new{attemptId=attempt,leaseToken=lease}),default)).Error?.Code);
            var retry=await store.ExecuteAsync("Claim",null,null,null,default);
            var newClaim=await store.ExecuteAsync("Claim",job,WorkerInput(new{eventKey=retry.GetProperty("eventKey").GetString(),payloadHash=retry.GetProperty("payloadHash").GetString(),inputHash=IfcHash,toolchainVersion="fake-ifc-v1"}),default);
            Assert.True(newClaim.IsSuccess,newClaim.Error?.Code);
            var fresh=newClaim.Value.GetProperty("attemptId").GetGuid();var freshLease=newClaim.Value.GetProperty("leaseToken").GetGuid();Assert.NotEqual(attempt,fresh);
            var output=new{artifactType="preview_glb",objectKey=newClaim.Value.GetProperty("outputPrefix").GetString()+"preview.glb",sha256Hash=IfcHash,sizeBytes=100L,schemaVersion="1",metadata=new{},validatorVersion="fake-validator-v1",outcome="Passed",issues=Array.Empty<object>()};
            var registered=await store.ExecuteAsync("Output",job,WorkerInput(new{attemptId=fresh,leaseToken=freshLease,output}),default);Assert.True(registered.IsSuccess,registered.Error?.Code);
            var outputHash=registered.Value.GetProperty("outputHash").GetString();
            Assert.Equal("OUTPUT_CONFLICT",(await store.ExecuteAsync("Complete",job,WorkerInput(new{attemptId=fresh,leaseToken=freshLease,outputHash=new string('b',64)}),default)).Error?.Code);
            var complete=WorkerInput(new{attemptId=fresh,leaseToken=freshLease,outputHash});
            Assert.True((await store.ExecuteAsync("Complete",job,complete,default)).IsSuccess);
            Assert.True((await store.ExecuteAsync("Complete",job,complete,default)).IsSuccess);
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM revision_artifacts WHERE job_id='{job}'"));
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM validation_runs WHERE job_id='{job}' AND processing_attempt_id='{fresh}' AND outcome='Passed'"));
            Assert.Equal("Succeeded",await ScalarAsync($"SELECT status FROM processing_jobs WHERE id='{job}'"));
            client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
            foreach(var path in new[]{ $"/api/processing-jobs/{job}",$"/api/processing-jobs/{job}/qa",$"/api/revisions/{revision}/artifacts",$"/api/revisions/{revision}/issues",$"/api/revisions/{revision}/processing-logs" })
            {
                var response=await client.GetAsync(path);Assert.True(response.StatusCode==HttpStatusCode.OK,path+" "+await response.Content.ReadAsStringAsync());
            }
            Assert.Equal("NotClaimable",await ScalarAsync($"SELECT requeue_processing_job('{job}','new-after-success','Retry')"));
        });
    }
    [PostgresFact]
    public async Task Ifc_worker_output_audit_failure_rolls_back_acceptance_and_stale_attempt_cannot_publish()
    {
        var revision=await SeedVerifiedIfcRevision();
        await WithProcessingRuntime(async(api,worker)=>
        {
            await using var db=BuildingContext(api);var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:ProcessingExecutor"]=worker}).Build();var store=new ProcessingRuntimeStore(db,config);
            var job=(await store.RequestAsync(adminId,revision,"rollback",default)).Value;
            var e=await store.ExecuteAsync("Claim",null,null,null,default);
            var claim=(await store.ExecuteAsync("Claim",job,WorkerInput(new{eventKey=e.GetProperty("eventKey").GetString(),payloadHash=e.GetProperty("payloadHash").GetString(),inputHash=IfcHash,toolchainVersion="fake-ifc-v1"}),default)).Value;
            var attemptId=claim.GetProperty("attemptId").GetGuid();var leaseToken=claim.GetProperty("leaseToken").GetGuid();
            var output=new{artifactType="preview_glb",objectKey=claim.GetProperty("outputPrefix").GetString()+"preview.glb",sha256Hash=IfcHash,sizeBytes=100L,schemaVersion="1",metadata=new{},validatorVersion="fake-v1",outcome="Passed",issues=Array.Empty<object>()};
            var registered=(await store.ExecuteAsync("Output",job,WorkerInput(new{attemptId,leaseToken,output}),default)).Value;var outputHash=registered.GetProperty("outputHash").GetString();
            await ExecuteAsync("ALTER TABLE audit_logs ADD CONSTRAINT reject_processing_accept CHECK(target_entity<>'ProcessingJob' OR action<>'Update') NOT VALID");
            await Assert.ThrowsAsync<PostgresException>(()=>store.ExecuteAsync("Complete",job,WorkerInput(new{attemptId,leaseToken,outputHash}),default));
            Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM revision_artifacts WHERE job_id='{job}'"));Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM validation_runs WHERE job_id='{job}'"));
            Assert.Equal("Running",await ScalarAsync($"SELECT status FROM processing_jobs WHERE id='{job}'"));
            await ExecuteAsync("ALTER TABLE audit_logs DROP CONSTRAINT reject_processing_accept");
            Assert.True((await store.ExecuteAsync("Fail",job,WorkerInput(new{attemptId,leaseToken,reason="fake failure"}),default)).IsSuccess);
            Assert.Equal("Requeued",await ScalarAsync($"SELECT requeue_processing_job('{job}','retry-once','Retry')"));
            Assert.Equal("AlreadyRequeued",await ScalarAsync($"SELECT requeue_processing_job('{job}','retry-once','Retry')"));
            await Assert.ThrowsAsync<PostgresException>(()=>ExecuteAsync($"SELECT requeue_processing_job('{job}','retry-once','Changed')"));
            Assert.Equal("ATTEMPT_STALE",(await store.ExecuteAsync("Complete",job,WorkerInput(new{attemptId,leaseToken,outputHash}),default)).Error?.Code);
        });
    }
    [PostgresFact]
    public async Task Ifc_http_worker_rejects_user_bearer_and_dispatcher_recovers_lost_ack_without_duplicate_claim()
    {
        var revision=await SeedVerifiedIfcRevision();
        await WithProcessingRuntime(async(api,worker)=>
        {
            var key=new string('x',48);var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:ProcessingExecutor"]=worker}).Build();
            using var host=factory!.WithWebHostBuilder(web=>
            {
                web.ConfigureAppConfiguration((_,builder)=>builder.AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:ProcessingExecutor"]=worker }));
                web.ConfigureServices(services=>
                {
                    services.PostConfigure<ProcessingWorkerOptions>(o=>{o.WorkerApiEnabled=true;o.MachineKey=key;o.AllowedToolchains=["fake-v1"];});
                    services.RemoveAll<IProcessingWorkerGate>();services.AddScoped<IProcessingWorkerGate>(p=>new ProcessingRuntimeStore(p.GetRequiredService<Fire3D.Infrastructure.Persistence.Fire3DDbContext>(),config));
                });
            });
            using var http=host.CreateClient();
            http.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
            var unauthorized=await http.PostAsJsonAsync($"/internal/processing/jobs/{Guid.NewGuid()}/claim",new{});
            Assert.Equal(HttpStatusCode.Unauthorized,unauthorized.StatusCode);
            http.DefaultRequestHeaders.Authorization=null;http.DefaultRequestHeaders.Add("X-Worker-Key",key);
            await using var db=BuildingContext(api);var store=new ProcessingRuntimeStore(db,config);
            var job=(await store.RequestAsync(adminId,revision,"http-worker",default)).Value;
            var delivered=0;
            var handler=new FakeProcessingHttp(async request=>
            {
                Assert.Equal(key,request.Headers.GetValues("X-Worker-Key").Single());
                var envelope=(await request.Content!.ReadFromJsonAsync<JsonElement>())!;
                var response=await http.PostAsJsonAsync($"/internal/processing/jobs/{job}/claim",new{eventKey=envelope.GetProperty("eventKey").GetString(),payloadHash=envelope.GetProperty("payloadHash").GetString(),inputHash=IfcHash,toolchainVersion="fake-v1"});
                Assert.Equal(HttpStatusCode.OK,response.StatusCode);
                var claim=await response.Content.ReadFromJsonAsync<JsonElement>();
                if(delivered++==0)throw new HttpRequestException("Lost worker ACK after receipt commit");
                return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{eventKey=envelope.GetProperty("eventKey").GetString(),payloadHash=envelope.GetProperty("payloadHash").GetString(),receiptId=claim.GetProperty("receiptId").GetGuid()})};
            });
            var services=new ServiceCollection();services.AddScoped(_=>BuildingContext(api));services.AddScoped<IProcessingDispatchGate>(p=>new ProcessingRuntimeStore(p.GetRequiredService<Fire3D.Infrastructure.Persistence.Fire3DDbContext>(),config));
            using var provider=services.BuildServiceProvider();
            var clients=ResetProxy.For<IHttpClientFactory>((_,_)=>new HttpClient(handler,false));
            var dispatcher=new ProcessingDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),clients,Options.Create(new ProcessingWorkerOptions{DispatcherEnabled=true,WorkerUrl="https://worker.example.test/jobs",MachineKey=key}),NullLogger<ProcessingDispatcher>.Instance);
            Assert.False(await dispatcher.RunOnceAsync(default));
            await ExecuteAsync("UPDATE integration_outbox_events SET available_at=now()-interval '1 second'");
            Assert.True(await dispatcher.RunOnceAsync(default));
            Assert.Equal(2,delivered);Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM processing_job_attempts WHERE processing_job_id='{job}'"));
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM processing_delivery_receipts r JOIN integration_outbox_events e ON e.idempotency_key=r.event_key WHERE e.aggregate_id='{job}' AND e.status='Published'"));
        });
    }
    private async Task WithProcessingRuntime(Func<string,string,Task> action)
    {
        var api="processing_api_test_"+Guid.NewGuid().ToString("N");var worker="processing_machine_test_"+Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {api} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}'; CREATE ROLE {worker} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}'; GRANT USAGE ON SCHEMA public TO {api},{worker}; GRANT EXECUTE ON FUNCTION request_revision_processing(uuid,uuid,text),processing_dispatch_gate(text,text,uuid,uuid) TO {api}; GRANT fet3d_processing_executor TO {worker}");
        try
        {
            Assert.Equal(false,await ScalarAsync($"SELECT has_table_privilege('{worker}','revision_artifacts','INSERT')"));
            Assert.Equal(false,await ScalarAsync($"SELECT has_table_privilege('{api}','processing_jobs','INSERT')"));
            Assert.Equal(false,await ScalarAsync($"SELECT has_function_privilege('{api}','processing_worker_gate(text,uuid,jsonb)','EXECUTE')"));
            await action(new NpgsqlConnectionStringBuilder(testConnection){Username=api,Pooling=false}.ConnectionString,new NpgsqlConnectionStringBuilder(testConnection){Username=worker,Pooling=false}.ConnectionString);
        }
        finally { await ExecuteAsync($"DROP OWNED BY {api}; DROP ROLE {api}; DROP OWNED BY {worker}; DROP ROLE {worker}"); }
    }
}

internal sealed class FakeProcessingHttp(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request);
}
