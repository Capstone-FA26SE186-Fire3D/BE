using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Storage;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private static readonly byte[] IfcBytes = Encoding.UTF8.GetBytes("ISO-10303-21;HEADER;ENDSEC;DATA;ENDSEC;END-ISO-10303-21;");
    private static readonly string IfcHash = Convert.ToHexStringLower(SHA256.HashData(IfcBytes));
    private static readonly IOptions<IfcUploadOptions> IfcOptions = Options.Create(new IfcUploadOptions { Enabled = true, MaxBytes = 1024, CleanupEnabled = true });

    private async Task<Guid> SeedIfcBuilding()
    {
        var org = await SeedBuildingOrganization(); var building = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{building}','{org}','IFC test',true,'{adminId}',now(),now())");
        return building;
    }
    private static FinalizeIfcUploadRequest CompleteInput(InitiateIfcUploadResponse upload) => new(upload.ObjectKey, IfcBytes.Length, "application/octet-stream", IfcHash, "model.ifc");

    [PostgresFact]
    public async Task Ifc_intent_replay_conflict_and_complete_use_restricted_gates_without_direct_provenance_writes()
    {
        var building = await SeedIfcBuilding();
        await WithIfcRuntime(async runtime =>
        {
            var fake = new IfcMemoryStorage();
            await using var context = BuildingContext(runtime);
            var flow = new IfcUploadService(new IfcUploadStore(context), fake.Storage, fake.Inspector, IfcOptions, TimeProvider.System);
            var input = new InitiateIfcUploadRequest(IfcBytes.Length,"model.ifc","v1",IfcHash);
            var first = await flow.InitiateAsync(adminId, building, input, "ifc-test-1", default);
            Assert.True(first.IsSuccess,first.Error?.Code);
            var again = await flow.InitiateAsync(adminId, building, input, "ifc-test-1", default);
            Assert.Equal(first.Value!.RevisionId, again.Value!.RevisionId);
            Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", (await flow.InitiateAsync(adminId, building,input with { VersionLabel="v2" },"ifc-test-1",default)).Error?.Code);
            fake.Put(first.Value.ObjectKey,IfcBytes);
            var wrong = await flow.CompleteAsync(adminId,first.Value.RevisionId,CompleteInput(first.Value) with { ObjectKey="other/source.ifc" },default);
            Assert.Equal("IFC_UPLOAD_INPUT_CONFLICT",wrong.Error?.Code); Assert.Equal(0,fake.Inspections);
            fake.BeforeCopy = async key => Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM ifc_upload_attempts WHERE final_key='{key}'"));
            Assert.True((await flow.CompleteAsync(adminId,first.Value.RevisionId,CompleteInput(first.Value),default)).IsSuccess);
            // Simulate a lost HTTP response: discard first result, complete again with identical input.
            Assert.True((await flow.CompleteAsync(adminId,first.Value.RevisionId,CompleteInput(first.Value),default)).IsSuccess);
            Assert.Equal(1,fake.Copies);
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM source_documents WHERE revision_id='{first.Value.RevisionId}' AND upload_verified_at IS NOT NULL AND sha256_hash='{IfcHash}'"));
            Assert.Equal(2L,await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{first.Value.RevisionId}'"));
            Assert.Equal("IFC_UPLOAD_INPUT_CONFLICT",(await flow.CompleteAsync(adminId,first.Value.RevisionId,CompleteInput(first.Value) with { Sha256Hash=new string('a',64) },default)).Error?.Code);
        });
    }

    [PostgresFact]
    public async Task Ifc_complete_copy_then_audit_failure_preserves_candidate_and_retry_adopts_a_different_key()
    {
        var building=await SeedIfcBuilding();
        await WithIfcRuntime(async runtime =>
        {
            var fake=new IfcMemoryStorage();
            await using var context=BuildingContext(runtime);
            var flow=new IfcUploadService(new IfcUploadStore(context),fake.Storage,fake.Inspector,IfcOptions,TimeProvider.System);
            var upload=(await flow.InitiateAsync(adminId,building,new(IfcBytes.Length,"model.ifc","v1",IfcHash),"audit-rollback",default)).Value!;
            fake.Put(upload.ObjectKey,IfcBytes);
            await ExecuteAsync("ALTER TABLE audit_logs ADD CONSTRAINT reject_ifc_finalize CHECK(target_entity<>'revisions' OR action<>'Update') NOT VALID");
            await Assert.ThrowsAsync<PostgresException>(()=>flow.CompleteAsync(adminId,upload.RevisionId,CompleteInput(upload),default));
            Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM source_documents WHERE revision_id='{upload.RevisionId}'"));
            var orphan=(string)(await ScalarAsync($"SELECT final_key FROM ifc_upload_attempts WHERE revision_id='{upload.RevisionId}'"))!;
            Assert.True(fake.Objects.ContainsKey(orphan));
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM ifc_object_cleanup WHERE object_key='{orphan}'"));
            await ExecuteAsync($"ALTER TABLE audit_logs DROP CONSTRAINT reject_ifc_finalize; UPDATE ifc_upload_attempts SET lease_until=now()-interval '1 second' WHERE revision_id='{upload.RevisionId}'");
            Assert.True((await flow.CompleteAsync(adminId,upload.RevisionId,CompleteInput(upload),default)).IsSuccess);
            var winner=(string)(await ScalarAsync($"SELECT storage_url FROM source_documents WHERE revision_id='{upload.RevisionId}'"))!;
            Assert.NotEqual(orphan,winner);
            await ExecuteAsync("UPDATE ifc_object_cleanup SET available_at=now()-interval '1 second'");
            var services=new ServiceCollection(); services.AddScoped(_=>BuildingContext(runtime)); services.AddScoped<IStorageService>(_=>fake.Storage);
            using var provider=services.BuildServiceProvider();
            var worker=new IfcUploadCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(),IfcOptions,NullLogger<IfcUploadCleanupWorker>.Instance);
            fake.DeleteFailures=1;
            for(var i=0;i<4;i++) await worker.RunOnceAsync(default);
            await ExecuteAsync("UPDATE ifc_object_cleanup SET available_at=now()-interval '1 second'");
            for(var i=0;i<4;i++) await worker.RunOnceAsync(default);
            Assert.True(fake.Objects.ContainsKey(winner)); Assert.False(fake.Objects.ContainsKey(orphan));
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM ifc_object_cleanup WHERE object_key='{winner}'"));
            // A late timed-out write is found again because cleanup tombstones were not discarded.
            fake.Put(orphan,IfcBytes); await ExecuteAsync($"UPDATE ifc_object_cleanup SET available_at=now()-interval '1 second' WHERE object_key='{orphan}'");
            for(var i=0;i<4;i++) await worker.RunOnceAsync(default);
            Assert.False(fake.Objects.ContainsKey(orphan)); Assert.True(fake.Objects.ContainsKey(winner));
        });
    }

    [PostgresFact]
    public async Task Ifc_complete_race_and_staging_overwrite_cannot_adopt_two_sources()
    {
        var building=await SeedIfcBuilding();
        await WithIfcRuntime(async runtime =>
        {
            var fake=new IfcMemoryStorage(); await using var first=BuildingContext(runtime); await using var second=BuildingContext(runtime);
            var a=new IfcUploadService(new IfcUploadStore(first),fake.Storage,fake.Inspector,IfcOptions,TimeProvider.System);
            var b=new IfcUploadService(new IfcUploadStore(second),fake.Storage,fake.Inspector,IfcOptions,TimeProvider.System);
            var upload=(await a.InitiateAsync(adminId,building,new(IfcBytes.Length,"model.ifc","v1",IfcHash),"race",default)).Value!;
            fake.Put(upload.ObjectKey,IfcBytes);
            var copying=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.BeforeCopy=async _=> { copying.TrySetResult(); await release.Task; };
            var pending=a.CompleteAsync(adminId,upload.RevisionId,CompleteInput(upload),default);
            await copying.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try { Assert.Equal("IFC_UPLOAD_IN_PROGRESS",(await b.CompleteAsync(adminId,upload.RevisionId,CompleteInput(upload),default)).Error?.Code); }
            finally { release.TrySetResult(); }
            Assert.True((await pending).IsSuccess);
            Assert.Equal(1L,await ScalarAsync($"SELECT count(*) FROM source_documents WHERE revision_id='{upload.RevisionId}'"));
            var other=(await a.InitiateAsync(adminId,building,new(IfcBytes.Length,"model.ifc","v2",IfcHash),"overwrite",default)).Value!;
            fake.Put(other.ObjectKey,IfcBytes); fake.BeforeCopy=_=> { fake.Put(other.ObjectKey,Encoding.UTF8.GetBytes(new string('x',IfcBytes.Length)));return Task.CompletedTask; };
            Assert.Equal("IFC_SOURCE_CHANGED",(await a.CompleteAsync(adminId,other.RevisionId,CompleteInput(other),default)).Error?.Code);
            Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM source_documents WHERE revision_id='{other.RevisionId}'"));
        });
    }
    [PostgresFact]
    public async Task Ifc_wrong_actor_hash_size_and_expired_intent_are_rejected_without_source_writes()
    {
        var building=await SeedIfcBuilding();
        await WithIfcRuntime(async runtime=>
        {
            var fake=new IfcMemoryStorage(); await using var context=BuildingContext(runtime);
            var flow=new IfcUploadService(new IfcUploadStore(context),fake.Storage,fake.Inspector,IfcOptions,TimeProvider.System);
            var upload=(await flow.InitiateAsync(adminId,building,new(IfcBytes.Length,"model.ifc","v1",IfcHash),"negative",default)).Value!;
            fake.Put(upload.ObjectKey,Encoding.UTF8.GetBytes(new string('x',IfcBytes.Length)));
            Assert.Equal("IFC_SOURCE_MISMATCH",(await flow.CompleteAsync(adminId,upload.RevisionId,CompleteInput(upload),default)).Error?.Code);
            fake.Put(upload.ObjectKey,new byte[IfcBytes.Length+1]);
            Assert.Equal("IFC_SOURCE_MISMATCH",(await flow.CompleteAsync(adminId,upload.RevisionId,CompleteInput(upload),default)).Error?.Code);
            var other=Guid.NewGuid();
            await ExecuteAsync($"INSERT INTO users(id,email,role,is_active,created_at,updated_at) VALUES('{other}','other-ifc@example.test','PlatformAdmin',true,now(),now())");
            var inspected=fake.Inspections;
            Assert.Equal("IFC_UPLOAD_INTENT_NOT_FOUND",(await flow.CompleteAsync(other,upload.RevisionId,CompleteInput(upload),default)).Error?.Code);
            Assert.Equal(inspected,fake.Inspections);
            await ExecuteAsync($"UPDATE ifc_upload_intents SET expires_at=now()-interval '1 second' WHERE revision_id='{upload.RevisionId}'");
            Assert.Equal("IFC_UPLOAD_EXPIRED",(await flow.CompleteAsync(adminId,upload.RevisionId,CompleteInput(upload),default)).Error?.Code);
            Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM source_documents WHERE revision_id='{upload.RevisionId}'"));
            Assert.Equal(0L,await ScalarAsync($"SELECT count(*) FROM ifc_upload_attempts WHERE revision_id='{upload.RevisionId}'"));
        });
    }
    [PostgresFact]
    public async Task Ifc_http_upload_routes_share_the_same_intent_and_do_not_read_storage_for_wrong_key()
    {
        var building=await SeedIfcBuilding(); var fake=new IfcMemoryStorage();
        using var host=factory!.WithWebHostBuilder(web=>
        {
            web.ConfigureAppConfiguration((_,config)=>config.AddInMemoryCollection(new Dictionary<string,string?> { ["IfcUpload:Enabled"]="true",["IfcUpload:MaxBytes"]="1024",["IfcUpload:CleanupEnabled"]="true" }));
            web.ConfigureServices(services=>
            {
                services.PostConfigure<IfcUploadOptions>(o=> { o.Enabled=true;o.MaxBytes=1024;o.CleanupEnabled=true; });
                services.RemoveAll<IStorageService>(); services.AddScoped<IStorageService>(_=>fake.Storage);
                services.RemoveAll<IIfcSourceInspector>();services.AddScoped<IIfcSourceInspector>(_=>fake.Inspector);
            });
        });
        using var http=host.CreateClient(); http.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
        http.DefaultRequestHeaders.Add("Idempotency-Key","same-file");
        var input=new InitiateIfcUploadRequest(IfcBytes.Length,"model.ifc","v1",IfcHash);
        var first=await http.PostAsJsonAsync($"/api/buildings/{building}/ifc",input);
        Assert.True(first.StatusCode==HttpStatusCode.Created,await first.Content.ReadAsStringAsync());
        var upload=(await first.Content.ReadFromJsonAsync<InitiateIfcUploadResponse>())!;
        var second=await http.PostAsJsonAsync($"/api/buildings/{building}/revisions/upload-url",input);
        Assert.Equal(upload.RevisionId,(await second.Content.ReadFromJsonAsync<InitiateIfcUploadResponse>())!.RevisionId);
        var wrong=await http.PostAsJsonAsync($"/api/revisions/{upload.RevisionId}/upload-complete",CompleteInput(upload) with { ObjectKey="wrong.ifc" });
        Assert.Equal(HttpStatusCode.Conflict,wrong.StatusCode); Assert.Equal(0,fake.Inspections);
        fake.Put(upload.ObjectKey,IfcBytes);
        Assert.Equal(HttpStatusCode.NoContent,(await http.PostAsJsonAsync($"/api/revisions/{upload.RevisionId}/upload-complete",CompleteInput(upload))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await http.PostAsJsonAsync($"/api/revisions/{upload.RevisionId}/upload-complete",CompleteInput(upload))).StatusCode);
        Assert.Equal(1,fake.Copies);
    }
    private async Task WithIfcRuntime(Func<string,Task> action)
    {
        var role="ifc_upload_test_"+Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {role} LOGIN NOSUPERUSER NOBYPASSRLS; GRANT USAGE ON SCHEMA public TO {role}; GRANT EXECUTE ON FUNCTION ifc_upload_gate(text,uuid,uuid,jsonb,text,uuid,text),claim_ifc_object_cleanup(),finish_ifc_object_cleanup(text,uuid,boolean) TO {role}");
        try
        {
            Assert.Equal(false,await ScalarAsync($"SELECT has_table_privilege('{role}','source_documents','INSERT')"));
            Assert.Equal(false,await ScalarAsync($"SELECT has_table_privilege('{role}','ifc_upload_attempts','UPDATE')"));
            await action(new NpgsqlConnectionStringBuilder(testConnection){Username=role,Password="",Pooling=false}.ConnectionString);
        }
        finally { await ExecuteAsync($"DROP OWNED BY {role}; DROP ROLE {role}"); }
    }
}

internal sealed class IfcMemoryStorage
{
    public ConcurrentDictionary<string,(byte[] Bytes,string Etag)> Objects { get; }=new();
    public int Copies,Inspections,DeleteFailures;
    public Func<string,Task>? BeforeCopy;
    public void Put(string key,byte[] bytes)=>Objects[key]=(bytes,Guid.NewGuid().ToString("N"));
    public IStorageService Storage => ResetProxy.For<IStorageService>((method,args)=>method switch
    {
        "GeneratePresignedUploadUrlAsync"=>Task.FromResult("https://storage.example.test/put"),
        "DeleteObjectAsync"=>Delete((string)args![0]!),
        _=>throw new InvalidOperationException(method)
    });
    private Task Delete(string key)
    {
        if(DeleteFailures-->0) throw new IOException("Simulated provider failure"); Objects.TryRemove(key,out _);return Task.CompletedTask;
    }
    public IIfcSourceInspector Inspector=>ResetProxy.For<IIfcSourceInspector>((method,args)=>method switch
    {
        "MetadataAsync"=>Task.FromResult<StorageObjectMetadata?>(Objects.TryGetValue((string)args![0]!,out var item)?new(item.Bytes.Length,"application/octet-stream",item.Etag):null),
        "InspectAsync"=>Inspect((string)args![0]!, (string)args[1]!),
        "CopyAsync"=>Copy((string)args![0]!, (string)args[1]!, (string)args[2]!),
        _=>throw new InvalidOperationException(method)
    });
    private Task<IfcObjectDigest?> Inspect(string key,string etag)
    {
        Inspections++; var item=Objects[key];return Task.FromResult<IfcObjectDigest?>(item.Etag==etag?new(item.Bytes.Length,Convert.ToHexStringLower(SHA256.HashData(item.Bytes)),etag):null);
    }
    private async Task<bool> Copy(string key,string etag,string final)
    {
        if(BeforeCopy is not null) await BeforeCopy(final);
        var item=Objects[key]; if(item.Etag!=etag)return false;
        Copies++; Put(final,item.Bytes); return true;
    }
}
