using System.Net;
using System.Text.Json;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class BillingV7OpenApiTests
{
    [BillingPostgresFact]
    public async Task Inventory_matches_OpenAPI_method_route_pairs_without_manual_count()
    {
        await using var database=await BillingDatabase.Create(migrationHistory:true);using var factory=new BillingApiTests.Factory(database);using var client=factory.CreateClient();
        using var doc=JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var actual=doc.RootElement.GetProperty("paths").EnumerateObject().SelectMany(path=>path.Value.EnumerateObject().Where(method=>new[]{"get","post","put","patch","delete","head","options","trace"}.Contains(method.Name)).Select(method=>method.Name.ToUpperInvariant()+" "+path.Name)).Order().ToArray();
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"docs","api-route-inventory.md")))directory=directory.Parent;
        Assert.NotNull(directory);
        var text=await File.ReadAllTextAsync(Path.Combine(directory!.FullName,"docs","api-route-inventory.md"));
        var documented=System.Text.RegularExpressions.Regex.Matches(text,@"(?m)^\| (GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|TRACE) \| ([^|]+) \|").Select(m=>m.Groups[1].Value+" "+m.Groups[2].Value.Trim()).Order().ToArray();
        Assert.Equal(actual,documented);
    }
    [BillingPostgresFact]
    public async Task Reporting_readers_work_with_restricted_api_role_without_ledger_DML()
    {
        await using var db=await BillingDatabase.Create(migrationHistory:true);
        var role="report_read_"+Guid.NewGuid().ToString("N");
        await db.Sql($"CREATE ROLE {role} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{(new Npgsql.NpgsqlConnectionStringBuilder(db.Connection).Password??"").Replace("'","''")}';GRANT fire3d_api TO {role};GRANT USAGE ON SCHEMA public TO {role}");
        try
        {
            await using var context=db.Context(new Npgsql.NpgsqlConnectionStringBuilder(db.Connection){Username=role}.ConnectionString);
            var audit=await new Fire3D.Infrastructure.Reporting.AuditQueries(context).List(BillingDatabase.Admin,BillingDatabase.Admin,new(),default);
            Assert.True(audit.IsSuccess,audit.Error?.Code);
            var snapshot=await new Fire3D.Infrastructure.Reporting.OperationsQueries(context).Platform(BillingDatabase.Admin,BillingDatabase.Admin,null,null,default);
            Assert.True(snapshot.IsSuccess,snapshot.Error?.Code);
            Assert.Equal(false,await db.Scalar($"SELECT has_table_privilege('{role}','payment_transactions','INSERT')"));
        }
        finally{await db.Sql($"DROP OWNED BY {role};DROP ROLE {role}");}
    }
    [BillingPostgresFact]
    public async Task Success_status_DTO_headers_roles_and_health_match_runtime_contract()
    {
        await using var database=await BillingDatabase.Create(migrationHistory:true);using var factory=new BillingApiTests.Factory(database);using var client=factory.CreateClient();
        using var document=JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));var root=document.RootElement;var paths=root.GetProperty("paths");
        foreach(var(path,method,status,schema) in new[]{
            ("/api/auth/refresh","post","200","TokenResponse"),("/api/accounts","post","201","AccountResponse"),
            ("/api/organizations","post","201","OrganizationResponse"),("/api/billing/service-packages/{id}","get","200","PackageResponse"),
            ("/api/billing/quotations/{id}","get","200","QuotationResponse"),("/api/billing/quotations/{id}","patch","200","QuotationResponse"),
            ("/api/payments/payos/checkouts/{id}","get","200","PayosCheckoutResponse"),("/api/payments/payos/requests/{id}","get","200","PayosPaymentResponse"),
            ("/api/admin/analytics/operations","get","200","PlatformOperations"),("/api/organizations/me/analytics/operations","get","200","OrganizationOperations"),
            ("/api/admin/audit-logs/{id}","get","200","AuditDetail"),("/api/admin/billing/quota-policies","post","201","QuotaPolicyResponse")})
            Assert.Contains(schema,paths.GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty(status).GetProperty("content").GetRawText());
        Assert.True(paths.GetProperty("/api/auth/refresh").GetProperty("post").GetProperty("responses").GetProperty("429").GetProperty("headers").TryGetProperty("Retry-After",out _));
        Assert.True(paths.GetProperty("/api/billing/quotations/{id}").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag",out _));
        var schemas=root.GetProperty("components").GetProperty("schemas");
        foreach(var field in new[]{"commercialVersion","learnerLimit","aiQuotaUnits","aiPolicyVersionId","pricingBasis","isPurchasable"})Assert.True(schemas.GetProperty("PackageResponse").GetProperty("properties").TryGetProperty(field,out _));
        Assert.False(schemas.GetProperty("OrganizationOperations").GetProperty("properties").TryGetProperty("accounts",out _));
        var org=paths.GetProperty("/api/organizations/me/analytics/operations").GetProperty("get");Assert.Contains("OrganizationUser",org.GetProperty("description").GetString());Assert.DoesNotContain("PlatformAdministration",org.GetProperty("description").GetString());
        Assert.Equal("/",root.GetProperty("servers")[0].GetProperty("url").GetString());
        Assert.True(paths.GetProperty("/api/releases/{releaseId}/publish").GetProperty("post").GetProperty("responses").TryGetProperty("204",out _));
        using var version=JsonDocument.Parse(await client.GetStringAsync("/health/version"));Assert.NotEqual("unknown",version.RootElement.GetProperty("version").GetString());Assert.Single(version.RootElement.EnumerateObject());
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/admin/analytics/operations")).StatusCode);
        var output=Environment.GetEnvironmentVariable("FIRE3D_OPENAPI_OUTPUT");if(output is not null)await File.WriteAllTextAsync(output,root.GetRawText());
    }
}
