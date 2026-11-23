using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Fire3D.Application.Storage;
using Fire3D.Infrastructure.Ifc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Fire3D.AuthTests;

// #214: upload-complete checks FILE_SCHEMA, a rejected upload leaves no Draft revision or intent, adopted sources are Accepted.
public sealed partial class AuthIntegrationTests
{
    // The caller disposes the context: a runtime login connection left open blocks dropping the test database.
    private static (IfcUploadService Flow, IfcMemoryStorage Fake) IfcFlow(Fire3D.Infrastructure.Persistence.Fire3DDbContext context)
    {
        var fake = new IfcMemoryStorage();
        return (new IfcUploadService(new IfcUploadStore(context), fake.Storage, fake.Inspector, IfcOptions, TimeProvider.System), fake);
    }

    private static (byte[] Bytes, string Hash) IfcFile(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [PostgresFact]
    public async Task Ifc_renamed_zip_is_rejected_and_leaves_no_draft_revision_intent_or_live_staging_object()
    {
        var building = await SeedIfcBuilding();
        await WithIfcRuntime(async runtime =>
        {
            await using var flowContext = BuildingContext(runtime);
            var (flow, fake) = IfcFlow(flowContext);
            var (zip, zipHash) = IfcFile("PK\u0003\u0004 a renamed archive, not IFC");
            var upload = (await flow.InitiateAsync(adminId, building, new(zip.Length, "renamed.ifc", "v1", zipHash), "zip-cleanup", default)).Value!;
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM revisions WHERE id='{upload.RevisionId}' AND status='Draft'"));
            fake.Put(upload.ObjectKey, zip);
            var input = new Fire3D.Application.Ifc.Commands.FinalizeUpload.FinalizeIfcUploadRequest(upload.ObjectKey, zip.Length, "application/octet-stream", zipHash, "renamed.ifc");
            var completed = await flow.CompleteAsync(adminId, upload.RevisionId, input, default);
            Assert.Equal(("IFC_UNSUPPORTED_FORMAT", 422), (completed.Error?.Code, completed.Error?.Status));
            Assert.Equal(0, fake.Copies);
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM revisions WHERE id='{upload.RevisionId}'"));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM ifc_upload_intents WHERE revision_id='{upload.RevisionId}'"));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM revisions WHERE building_id='{building}'"));
            // The staging object is queued for deletion now instead of after the 60-minute intent expiry.
            Assert.Equal(true, await ScalarAsync($"SELECT available_at<=clock_timestamp() FROM ifc_object_cleanup WHERE object_key='{upload.ObjectKey}'"));
            Assert.Equal("Rejected", await ScalarAsync($"SELECT new_values->>'quarantineStatus' FROM audit_logs WHERE target_id='{upload.RevisionId}' AND action='Reject'"));
            Assert.Equal("IFC_UNSUPPORTED_FORMAT", await ScalarAsync($"SELECT new_values->>'code' FROM audit_logs WHERE target_id='{upload.RevisionId}' AND action='Reject'"));
            // A replay finds nothing to complete; the cleanup worker may now delete the staging object.
            var replay = await flow.CompleteAsync(adminId, upload.RevisionId, input, default);
            Assert.Equal(("IFC_UPLOAD_INTENT_NOT_FOUND", 404), (replay.Error?.Code, replay.Error?.Status));
            var services = new ServiceCollection(); services.AddScoped(_ => BuildingContext(runtime)); services.AddScoped<IStorageService>(_ => fake.Storage);
            using var provider = services.BuildServiceProvider();
            var worker = new IfcUploadCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(), IfcOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<IfcUploadCleanupWorker>.Instance);
            Assert.True(await worker.RunOnceAsync(default));
            Assert.False(fake.Objects.ContainsKey(upload.ObjectKey));
        });
    }

    [PostgresFact]
    public async Task Ifc_unsupported_file_schema_is_rejected_with_422_and_the_draft_revision_is_removed()
    {
        var building = await SeedIfcBuilding();
        await WithIfcRuntime(async runtime =>
        {
            await using var flowContext = BuildingContext(runtime);
            var (flow, fake) = IfcFlow(flowContext);
            foreach (var schema in new[] { "AP214", "IFC4X1" })
            {
                var (bytes, hash) = IfcFile(IfcModel(schema));
                var upload = (await flow.InitiateAsync(adminId, building, new(bytes.Length, "model.ifc", "v-" + schema, hash), "schema-" + schema, default)).Value!;
                fake.Put(upload.ObjectKey, bytes);
                var result = await flow.CompleteAsync(adminId, upload.RevisionId, new(upload.ObjectKey, bytes.Length, "application/octet-stream", hash, "model.ifc"), default);
                Assert.Equal(("IFC_UNSUPPORTED_SCHEMA", 422), (result.Error?.Code, result.Error?.Status));
                Assert.Equal(new[] { schema }, result.Error!.Errors!["fileSchema"]);
                Assert.Equal(new[] { "IFC2X3", "IFC4", "IFC4X3" }, result.Error.Errors["supportedSchemas"]);
                Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM revisions WHERE id='{upload.RevisionId}'"));
                Assert.Equal(schema, await ScalarAsync($"SELECT new_values#>>'{{details,fileSchema,0}}' FROM audit_logs WHERE target_id='{upload.RevisionId}' AND action='Reject'"));
            }
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM ifc_upload_intents"));
            Assert.Equal(0, fake.Copies);
        });
    }

    [PostgresFact]
    public async Task Ifc_supported_schema_variants_are_adopted_with_quarantine_accepted()
    {
        var building = await SeedIfcBuilding();
        await WithIfcRuntime(async runtime =>
        {
            await using var flowContext = BuildingContext(runtime);
            var (flow, fake) = IfcFlow(flowContext);
            foreach (var schema in new[] { "IFC2X3", "ifc4", "IFC4X3_ADD2" })
            {
                var (bytes, hash) = IfcFile(IfcModel(schema));
                var upload = (await flow.InitiateAsync(adminId, building, new(bytes.Length, "model.ifc", "v-" + schema, hash), "ok-" + schema, default)).Value!;
                fake.Put(upload.ObjectKey, bytes);
                var result = await flow.CompleteAsync(adminId, upload.RevisionId, new(upload.ObjectKey, bytes.Length, "application/octet-stream", hash, "model.ifc"), default);
                Assert.True(result.IsSuccess, result.Error?.Code);
                Assert.Equal("Accepted", await ScalarAsync($"SELECT quarantine_status::text FROM source_documents WHERE revision_id='{upload.RevisionId}'"));
                Assert.Equal("Uploaded", await ScalarAsync($"SELECT status::text FROM revisions WHERE id='{upload.RevisionId}'"));
            }
        });
    }

    [PostgresFact]
    public async Task Ifc_reject_never_removes_a_completed_upload()
    {
        var building = await SeedIfcBuilding();
        await WithIfcRuntime(async runtime =>
        {
            await using var flowContext = BuildingContext(runtime);
            var (flow, fake) = IfcFlow(flowContext);
            var upload = (await flow.InitiateAsync(adminId, building, new(IfcBytes.Length, "model.ifc", "v1", IfcHash), "completed-reject", default)).Value!;
            fake.Put(upload.ObjectKey, IfcBytes);
            Assert.True((await flow.CompleteAsync(adminId, upload.RevisionId, CompleteInput(upload), default)).IsSuccess);
            await using var context = BuildingContext(runtime);
            var reject = await new IfcUploadStore(context).RejectAsync(adminId, upload.RevisionId, CompleteInput(upload), new("IFC_UNSUPPORTED_SCHEMA", "x"), default);
            // The completed intent answers 409 (its completion receipt differs from the rejection input) and is never touched.
            Assert.Equal(409, reject.Error?.Status);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM source_documents WHERE revision_id='{upload.RevisionId}'"));
            // The gate only accepts the content codes of upload-complete.
            var other = (await flow.InitiateAsync(adminId, building, new(IfcBytes.Length, "model.ifc", "v2", IfcHash), "unknown-reject", default)).Value!;
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => new IfcUploadStore(context).RejectAsync(adminId, other.RevisionId, CompleteInput(other), new("NOT_A_CODE", "x"), default));
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM revisions WHERE id='{other.RevisionId}'"));
        });
    }

    [PostgresFact]
    public async Task Ifc_initiate_over_max_bytes_creates_nothing_and_upload_limits_are_published()
    {
        var building = await SeedIfcBuilding(); var fake = new IfcMemoryStorage();
        using var host = factory!.WithWebHostBuilder(web => web.ConfigureServices(services =>
        {
            services.PostConfigure<IfcUploadOptions>(o => { o.Enabled = true; o.MaxBytes = 1024; o.CleanupEnabled = true; });
            services.RemoveAll<IStorageService>(); services.AddScoped<IStorageService>(_ => fake.Storage);
            services.RemoveAll<IIfcSourceInspector>(); services.AddScoped<IIfcSourceInspector>(_ => fake.Inspector);
        }));
        using var http = host.CreateClient(); http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        using var limits = JsonDocument.Parse(await http.GetStringAsync("/api/ifc/upload-limits"));
        Assert.Equal(1024, limits.RootElement.GetProperty("maxBytes").GetInt64());
        Assert.True(limits.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(new[] { ".ifc" }, limits.RootElement.GetProperty("acceptedExtensions").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "IFC2X3", "IFC4", "IFC4X3" }, limits.RootElement.GetProperty("acceptedSchemas").EnumerateArray().Select(x => x.GetString()));
        http.DefaultRequestHeaders.Add("Idempotency-Key", "too-large");
        var response = await http.PostAsJsonAsync($"/api/buildings/{building}/ifc", new InitiateIfcUploadRequest(1025, "model.ifc", "v1", IfcHash));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_ERROR", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("fileSizeBytes", out _));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM revisions WHERE building_id='{building}'"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM ifc_upload_intents"));
    }

    [PostgresFact]
    public async Task Ifc_upload_limits_default_to_500_mib_and_require_a_signed_in_editor()
    {
        using var anonymous = factory!.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ifc/upload-limits")).StatusCode);
        Assert.Equal(524_288_000L, new IfcUploadOptions().MaxBytes);
        Assert.Equal(524_288_000L, IfcUploadLimitsResponse.From(new IfcUploadOptions { Enabled = true }).MaxBytes);
        Assert.False(IfcUploadLimitsResponse.From(new IfcUploadOptions { Enabled = true, MaxBytes = null }).Enabled);
    }
}
