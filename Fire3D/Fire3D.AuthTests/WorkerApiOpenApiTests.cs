using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Fire3D.API.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class WorkerApiOpenApiTests
{
    [BillingPostgresFact]
    public async Task Required_header_metadata_and_selected_response_schemas_match_OpenAPI()
    {
        await using var database=await BillingDatabase.Create(migrationHistory:true);
        using var factory=new BillingApiTests.Factory(database);using var client=factory.CreateClient();
        using var doc=JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));var paths=doc.RootElement.GetProperty("paths");
        var controllers=typeof(Program).Assembly.GetTypes().Where(t=>typeof(ControllerBase).IsAssignableFrom(t));
        foreach(var method in controllers.SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly)))
            foreach(var parameter in method.GetParameters().Where(p=>p.GetCustomAttribute<FromHeaderAttribute>() is not null))
                Assert.NotNull(parameter.GetCustomAttribute<RequiredRequestHeaderAttribute>());
        var put=paths.GetProperty("/api/revisions/{revisionId}/annotations").GetProperty("put");
        Assert.True(Assert.Single(put.GetProperty("parameters").EnumerateArray(),p=>p.GetProperty("name").GetString()=="If-Match").GetProperty("required").GetBoolean());
        foreach(var verb in new[]{"get","put"})Assert.True(paths.GetProperty("/api/revisions/{revisionId}/annotations").GetProperty(verb).GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag",out _));
        foreach(var status in new[]{"400","412","428"})Assert.True(put.GetProperty("responses").TryGetProperty(status,out _));
        foreach(var (path,status,schema) in new[]{
            ("/api/scenario-versions/{id}/submit","201","ContentReviewResponse"),
            ("/api/admin/scenario-versions/{id}/approve","200","ContentReviewResponse"),
            ("/api/admin/scenario-versions/{id}/reject","200","ContentReviewResponse"),
            ("/api/revisions/{revisionId}/confirm-for-training","200","ConfirmationResponse"),
            ("/api/revisions/{revisionId}/reviews","201","TechnicalRejectionResponse")})
        {
            var operation=paths.GetProperty(path).GetProperty("post");
            Assert.Contains(schema,operation.GetProperty("responses").GetProperty(status).GetProperty("content").GetRawText());
            Assert.True(operation.GetProperty("responses").TryGetProperty("401",out _));
        }
        Assert.True(paths.GetProperty("/api/buildings/{buildingId}/editor-preview").GetProperty("get").GetProperty("responses").TryGetProperty("503",out _));
    }
}

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Annotations_HTTP_preconditions_match_documented_headers()
    {
        var revision=await SeedVerifiedIfcRevision();client.DefaultRequestHeaders.Authorization=new("Bearer",(await LoginAsync()).AccessToken);
        var path=$"/api/revisions/{revision}/annotations";
        var get=await client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,get.StatusCode);Assert.Equal("\"0\"",get.Headers.ETag?.Tag);
        Assert.Equal((HttpStatusCode)428,(await client.PutAsJsonAsync(path,new {items=Array.Empty<object>()})).StatusCode);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match","invalid");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(path,new {items=Array.Empty<object>()})).StatusCode);
        client.DefaultRequestHeaders.Remove("If-Match");client.DefaultRequestHeaders.Add("If-Match",get.Headers.ETag!.Tag);
        var updated=await client.PutAsJsonAsync(path,new {items=Array.Empty<object>()});
        Assert.Equal(HttpStatusCode.OK,updated.StatusCode);Assert.Equal("\"1\"",updated.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed,(await client.PutAsJsonAsync(path,new {items=Array.Empty<object>()})).StatusCode);
        client.DefaultRequestHeaders.Remove("If-Match");client.DefaultRequestHeaders.Add("If-Match",updated.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(path,new{items=new[]{new {id=Guid.NewGuid(),ifcGlobalId="foreign-anchor",label="Room"}}})).StatusCode);
    }
}
