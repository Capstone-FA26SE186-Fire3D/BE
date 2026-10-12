using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task<JsonElement> OpenApiRoot()=>JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json")).RootElement.Clone();
    private static IEnumerable<string?> Names(JsonElement array)=>array.EnumerateArray().Select(x=>x.GetString());

    [PostgresFact]
    public async Task CORS_exposes_frontend_headers_and_OpenAPI_matches_upload_snapshot_and_rate_limit_runtime()
    {
        var origin=Fire3D.API.Configuration.CorsOriginConfiguration.GetAllowedOrigins(factory!.Services.GetRequiredService<IConfiguration>())[0];
        using(var request=new HttpRequestMessage(HttpMethod.Get,"/health"))
        {
            request.Headers.Add("Origin",origin);
            var response=await client.SendAsync(request);
            var exposed=string.Join(",",response.Headers.GetValues("Access-Control-Expose-Headers")).Split(',').Select(x=>x.Trim()).ToArray();
            foreach(var header in new[]{"ETag","Retry-After","X-Correlation-ID","Location"})Assert.Contains(header,exposed,StringComparer.OrdinalIgnoreCase);
        }
        var root=await OpenApiRoot();var paths=root.GetProperty("paths");var schemas=root.GetProperty("components").GetProperty("schemas");
        // The upload hash is required at runtime, so the schema says so.
        var upload=schemas.GetProperty("InitiateIfcUploadRequest");
        Assert.Contains("sha256Hash",Names(upload.GetProperty("required")));
        Assert.Equal("^[0-9a-fA-F]{64}$",upload.GetProperty("properties").GetProperty("sha256Hash").GetProperty("pattern").GetString());
        // Snapshot needs If-Match and Idempotency-Key, so its precondition and conflict responses are documented.
        var snapshot=paths.GetProperty("/api/scenario-drafts/{draftId}/snapshot").GetProperty("post").GetProperty("responses");
        foreach(var status in new[]{"409","412","428"})Assert.True(snapshot.TryGetProperty(status,out _),status);
        // Administration endpoints document their 429 with Retry-After.
        var limited=paths.GetProperty("/api/accounts").GetProperty("get").GetProperty("responses").GetProperty("429");
        Assert.True(limited.GetProperty("headers").TryGetProperty("Retry-After",out _));
    }

    [PostgresFact]
    public async Task OpenAPI_publishes_processing_enums_retry_key_and_deprecated_building_query()
    {
        var root=await OpenApiRoot();var schemas=root.GetProperty("components").GetProperty("schemas");
        string?[] Values(string schema,string property)=>schemas.GetProperty(schema).GetProperty("properties").GetProperty(property).GetProperty("enum").EnumerateArray().Select(x=>x.GetString()).ToArray();
        Assert.Equal(["Queued","Running","Succeeded","Failed","Cancelled"],Values("ProcessingJobResponse","status"));
        Assert.Equal(["Running","Succeeded","Failed","Expired"],Values("ProcessingAttemptResponse","status"));
        Assert.Equal(["Passed","Failed"],Values("ValidationRunResponse","outcome"));
        Assert.Equal(["Passed","Failed"],Values("ValidationRunResponse","status"));
        Assert.Equal(["Info","Warning","Error","Critical"],Values("RevisionIssueResponse","severity"));
        Assert.Equal(["reason"],Names(schemas.GetProperty("RetryProcessingJobRequest").GetProperty("required")));
        var create=root.GetProperty("paths").GetProperty("/api/buildings").GetProperty("post").GetProperty("parameters").EnumerateArray().Single(x=>x.GetProperty("name").GetString()=="organizationId");
        Assert.True(create.GetProperty("deprecated").GetBoolean());
        var retry=root.GetProperty("paths").GetProperty("/api/processing-jobs/{jobId}/retry").GetProperty("post").GetProperty("parameters").EnumerateArray();
        Assert.Contains(retry,x=>x.GetProperty("name").GetString()=="Idempotency-Key");
    }
}
