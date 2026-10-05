using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fire3D.Application.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AuthOpenApiContractTests
{
    private static WebApplicationFactory<Program> Factory(BillingDatabase db) =>
        new BillingApiTests.Factory(db).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IStorageService>();
            services.AddSingleton(ResetProxy.For<IStorageService>((_, _) => throw new InvalidOperationException("Header/auth tests must not call S3")));
        }));
    private static HttpClient As(WebApplicationFactory<Program> factory, Guid actor)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Test-Actor", actor.ToString()); return client;
    }
    private static JsonElement Resolve(JsonElement schema, JsonElement document) =>
        schema.TryGetProperty("$ref", out var reference)
            ? document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/').Last()) : schema;
    private static JsonElement Operation(JsonElement doc, string route, string method) => doc.GetProperty("paths").GetProperty(route).GetProperty(method);
    private static JsonElement Body(JsonElement operation, JsonElement doc, string mime = "application/json") =>
        Resolve(operation.GetProperty("requestBody").GetProperty("content").GetProperty(mime).GetProperty("schema"), doc);

    [BillingPostgresFact]
    public async Task Organization_profile_schema_accepts_the_255_character_name_boundary()
    {
        await using var db = await BillingDatabase.Create(false); using var factory = Factory(db);
        using var client = factory.CreateClient(); using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var name = Body(Operation(json.RootElement, "/api/organizations/me", "patch"), json.RootElement).GetProperty("properties").GetProperty("name");
        Assert.Equal(255, name.GetProperty("maxLength").GetInt32());
    }

    [BillingPostgresFact]
    public async Task Optional_registration_and_onboarding_gender_remains_nullable_in_generated_schema()
    {
        await using var db = await BillingDatabase.Create(false); using var factory = Factory(db);
        using var client = factory.CreateClient(); using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var doc = json.RootElement;
        bool Nullable(JsonElement schema)
        {
            if (schema.TryGetProperty("type", out var type) && (type.ValueKind == JsonValueKind.String && type.GetString() == "null"
                || type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(x => x.GetString() == "null")))
                return !schema.TryGetProperty("enum", out var values) || values.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Null);
            if (schema.TryGetProperty("anyOf", out var choices) && choices.EnumerateArray().Any(Nullable)) return true;
            return schema.TryGetProperty("$ref", out _) && Nullable(Resolve(schema, doc));
        }
        foreach (var route in new[] { "/api/auth/register/trainee", "/api/auth/register/organization", "/api/auth/google/onboarding/complete" })
            Assert.True(Nullable(Body(Operation(doc, route, "post"), doc).GetProperty("properties").GetProperty("gender")), route);
    }

    [BillingPostgresFact]
    public async Task Auth_checklist_contains_exactly_the_routes_exposed_by_openapi()
    {
        await using var db = await BillingDatabase.Create(false); using var factory = Factory(db);
        using var client = factory.CreateClient(); using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var actual = json.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.StartsWith("/api/auth/", StringComparison.Ordinal) || path.Name.StartsWith("/api/me/avatar", StringComparison.Ordinal)
                || path.Name is "/api/me/link-google" or "/api/organizations/me")
            .SelectMany(path => path.Value.EnumerateObject().Where(method => method.Name is "get" or "post" or "put" or "patch" or "delete")
                .Select(method => method.Name.ToUpperInvariant() + " " + path.Name)).OrderBy(x => x).ToArray();
        var markdown = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "auth-api-checklist.md"));
        var documented = Regex.Matches(markdown, @"(?m)^\| (GET|POST|PUT|PATCH|DELETE) \| `([^`]+)` \|")
            .Select(match => match.Groups[1].Value + " " + match.Groups[2].Value).OrderBy(x => x).ToArray();
        Assert.Equal(actual, documented);
        Assert.Contains("**" + actual.Length + " cặp method–route**", markdown);
    }

    [BillingPostgresFact]
    public async Task Registration_required_proof_enum_names_and_profile_error_statuses_are_documented()
    {
        await using var db = await BillingDatabase.Create(false); using var factory = Factory(db);
        using var client = factory.CreateClient(); using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var doc = json.RootElement;
        foreach (var route in new[] { "/api/auth/register", "/api/auth/register/trainee", "/api/auth/register/organization" })
        {
            var request = Body(Operation(doc, route, "post"), doc);
            Assert.Contains("registrationToken", request.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        }
        var role = doc.GetProperty("components").GetProperty("schemas").GetProperty("UserRole");
        Assert.Equal("string", role.GetProperty("type").GetString());
        Assert.Contains("OrganizationUser", role.GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        foreach (var route in new[] { "/api/auth/me", "/api/organizations/me" })
        {
            var responses = Operation(doc, route, "patch").GetProperty("responses");
            Assert.True(responses.TryGetProperty("412", out _)); Assert.True(responses.TryGetProperty("428", out _));
        }
    }

    [BillingPostgresFact]
    public async Task Converter_backed_profile_schemas_expose_mutable_fields_and_null_semantics()
    {
        await using var db = await BillingDatabase.Create(false); using var factory = Factory(db);
        using var client = factory.CreateClient(); using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var personal = Body(Operation(json.RootElement, "/api/auth/me", "patch"), json.RootElement);
        var properties = personal.GetProperty("properties");
        Assert.Equal(new[] { "dob", "fullName", "gender", "phoneNumber", "username" }, properties.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal("date", properties.GetProperty("dob").GetProperty("format").GetString());
        Assert.Contains("null", properties.GetProperty("dob").GetProperty("type").EnumerateArray().Select(x => x.GetString()));
        Assert.False(personal.TryGetProperty("required", out var required) && required.GetArrayLength() > 0);
        var organization = Body(Operation(json.RootElement, "/api/organizations/me", "patch"), json.RootElement).GetProperty("properties");
        Assert.Equal(new[] { "address", "name", "phoneNumber" }, organization.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal("string", organization.GetProperty("address").GetProperty("type").GetString());
    }

    [BillingPostgresFact]
    public async Task Multipart_file_and_required_auth_headers_match_swagger_input_controls()
    {
        await using var db = await BillingDatabase.Create(false); using var factory = Factory(db);
        using var client = factory.CreateClient(); using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var doc = json.RootElement;
        var upload = Body(Operation(doc, "/api/me/avatar/upload", "post"), doc, "multipart/form-data");
        var file = Resolve(upload.GetProperty("properties").GetProperty("file"), doc);
        Assert.Equal("string", file.GetProperty("type").GetString()); Assert.Equal("binary", file.GetProperty("format").GetString());
        Assert.Contains("file", upload.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        foreach (var (route, method, header) in new[]
        {
            ("/api/auth/me", "patch", "If-Match"), ("/api/organizations/me", "patch", "If-Match"),
            ("/api/me/avatar/upload", "post", "If-Match"), ("/api/me/avatar/complete", "post", "If-Match"),
            ("/api/me/avatar", "delete", "If-Match"), ("/api/auth/devices", "put", "X-Installation-Key"),
            ("/api/auth/devices/{deviceUuid}", "delete", "X-Installation-Key")
        })
        {
            var parameter = Operation(doc, route, method).GetProperty("parameters").EnumerateArray().Single(x => x.GetProperty("name").GetString() == header);
            Assert.True(parameter.GetProperty("required").GetBoolean(), route + " requires " + header);
        }
    }

    [BillingPostgresFact]
    public async Task Auth_security_and_organization_role_descriptions_follow_endpoint_metadata()
    {
        await using var db = await BillingDatabase.Create(false); using var factory = Factory(db);
        using var client = factory.CreateClient(); using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var doc = json.RootElement;
        Assert.Equal("/", doc.GetProperty("servers")[0].GetProperty("url").GetString());
        foreach (var route in new[] { "/api/auth/login", "/api/auth/registration/request-otp", "/api/auth/registration/verify-otp", "/api/auth/resend-verification", "/api/auth/register/trainee", "/api/auth/register/organization" })
        {
            var operation = Operation(doc, route, "post");
            Assert.True(!operation.TryGetProperty("security", out var security) || security.GetArrayLength() == 0, route);
        }
        foreach (var method in new[] { "get", "patch" })
        {
            var operation = Operation(doc, "/api/organizations/me", method);
            Assert.NotEmpty(operation.GetProperty("security").EnumerateArray());
            Assert.Contains("OrganizationUser", operation.GetProperty("description").GetString());
        }
    }

    [BillingPostgresFact]
    public async Task Http_rejects_missing_headers_and_unauthenticated_mutations_as_documented()
    {
        await using var db = await BillingDatabase.Create(false); using var factory = Factory(db);
        using var anonymous = factory.CreateClient(); using var owner = As(factory, BillingDatabase.Owner); using var admin = As(factory, BillingDatabase.Admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/avatar")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/organizations/me")).StatusCode);
        foreach (var route in new[] { "/api/auth/me", "/api/organizations/me" })
            Assert.Equal((HttpStatusCode)428, (await owner.PatchAsJsonAsync(route, new { fullName = "Test", name = "Test" })).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await owner.DeleteAsync("/api/me/avatar")).StatusCode);
        var response = await owner.DeleteAsync("/api/auth/devices/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(error.RootElement.TryGetProperty("code", out _)); Assert.True(error.RootElement.TryGetProperty("traceId", out _));
    }
}
