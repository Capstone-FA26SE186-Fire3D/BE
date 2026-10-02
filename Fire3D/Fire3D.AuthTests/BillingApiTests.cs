using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fire3D.API.Extensions;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingApiTests
{
    internal sealed class Factory(BillingDatabase database,Fire3D.Application.Billing.IPayosProvider? payos=null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["ConnectionStrings:DefaultConnection"]=database.Connection,
                ["Jwt:Key"]=new string('k',64), ["Jwt:Issuer"]="billing-tests", ["Jwt:Audience"]="billing-tests",
                ["AuthEmail:WorkerEnabled"]="false", ["AuthEmail:FrontendUrl"]="https://fet3d.io.vn",
                ["AuthEmail:VerificationUrl"]="https://fet3d.io.vn", ["PayOS:Enabled"]=(payos is not null).ToString(),
                ["PayOS:WorkerEnabled"]="false",["PayOS:ClientId"]="test-client",["PayOS:ApiKey"]="test-api",
                ["PayOS:ChecksumKey"]="test-checksum",["PayOS:ReturnUrl"]="https://api.example.test/billing/payment-return/",
                ["PayOS:CancelUrl"]="https://api.example.test/billing/payment-cancel/",
                ["ConnectionStrings:PayosRequestExecutor"]=database.RequestConnection,
                ["ConnectionStrings:PayosWebhookExecutor"]=database.WebhookConnection
            }));
            builder.ConfigureServices(services =>
            {
                if(payos is not null){services.RemoveAll<Fire3D.Application.Billing.IPayosProvider>();services.AddSingleton(payos);}
                services.RemoveAll<Fire3DDbContext>();
                services.RemoveAll<DbContextOptions<Fire3DDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<Fire3DDbContext>>();
                services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
                    { ["ConnectionStrings:DefaultConnection"]=database.Connection }).Build());
                foreach (var service in services.Where(x=>x.ServiceType==typeof(IHostedService)).ToArray())
                    services.Remove(service);
                services.AddAuthentication(options=>
                { options.DefaultAuthenticateScheme="BillingTest"; options.DefaultChallengeScheme="BillingTest"; })
                    .AddScheme<AuthenticationSchemeOptions,TestAuthentication>("BillingTest",_=>{});
                services.AddLogging(logging=>logging.ClearProviders());
            });
        }
        public HttpClient As(Guid actor)
        {
            var client=CreateClient(); client.DefaultRequestHeaders.Add("X-Test-Actor",actor.ToString()); return client;
        }
    }
    public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if(!Guid.TryParse(Request.Headers["X-Test-Actor"],out var actor)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity=new ClaimsIdentity(new[] {new Claim("sub",actor.ToString()),new Claim("sid",actor.ToString()),new Claim(ClaimTypes.Role,actor==BillingDatabase.Admin ? "PlatformAdmin":"OrganizationUser")},Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity),Scheme.Name)));
        }
    }
    private static async Task<JsonElement> Body(HttpResponseMessage response)
    { var text=await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode,text); return JsonDocument.Parse(text).RootElement.Clone(); }
    private static async Task<Guid> Package(HttpClient admin,string code="MONTH",decimal price=100)
    {
        var response=await admin.PostAsJsonAsync("/api/admin/service-packages",new {code,name="Monthly Building",unitPrice=price,durationMonths=1,isActive=true});
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        return (await Body(response)).GetProperty("id").GetGuid();
    }
    private static HttpRequestMessage QuoteRequest(Guid packageId,string key="quote-1")
    {
        var request=new HttpRequestMessage(HttpMethod.Post,"/api/billing/quotations")
        {Content=JsonContent.Create(new {items=new[]{new{buildingId=BillingDatabase.Building,servicePackageId=packageId,purchaseAction="New"}}})};
        request.Headers.Add("Idempotency-Key",key); return request;
    }

    [BillingPostgresFact]
    public async Task Catalog_permissions_validation_and_cross_tenant_quotation_are_enforced()
    {
        await using var database=await BillingDatabase.Create(); using var factory=new Factory(database);
        using var anonymous=factory.CreateClient(); using var admin=factory.As(BillingDatabase.Admin);
        using var owner=factory.As(BillingDatabase.Owner); using var other=factory.As(BillingDatabase.Other);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/billing/service-packages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.PostAsJsonAsync("/api/admin/service-packages",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync("/api/admin/service-packages",new{code="BAD",name="Bad",unitPrice=-1,durationMonths=1})).StatusCode);
        var package=await Package(admin);
        var foreign=await other.SendAsync(QuoteRequest(package));
        Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);
        await database.Sql($"UPDATE users SET is_active=false WHERE id='{BillingDatabase.Owner}'");
        Assert.Equal(HttpStatusCode.Unauthorized,(await owner.GetAsync("/api/billing/service-packages")).StatusCode);
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM quotations"));
    }

    [BillingPostgresFact]
    public async Task Concurrent_create_replays_once_and_conflicting_input_is_rejected()
    {
        await using var database=await BillingDatabase.Create(); using var factory=new Factory(database);
        using var admin=factory.As(BillingDatabase.Admin); using var owner=factory.As(BillingDatabase.Owner);
        var package=await Package(admin);
        var responses=await Task.WhenAll(owner.SendAsync(QuoteRequest(package)),owner.SendAsync(QuoteRequest(package)));
        foreach(var response in responses) Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        Assert.Equal((await Body(responses[0])).GetProperty("id").GetGuid(),(await Body(responses[1])).GetProperty("id").GetGuid());
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM quotations"));
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM billing_command_receipts"));
        var different=await Package(admin,"OTHER");
        Assert.Equal(HttpStatusCode.Conflict,(await owner.SendAsync(QuoteRequest(different))).StatusCode);
    }

    [BillingPostgresFact]
    public async Task Issued_snapshots_etag_and_single_acceptance_are_immutable()
    {
        await using var database=await BillingDatabase.Create(); using var factory=new Factory(database);
        using var admin=factory.As(BillingDatabase.Admin); using var owner=factory.As(BillingDatabase.Owner);
        var package=await Package(admin);
        var created=await owner.SendAsync(QuoteRequest(package));var quote=await Body(created);var id=quote.GetProperty("id").GetGuid();
        var issueBody=new {taxAmount=10,terms="One month Building service",validUntil=DateTimeOffset.UtcNow.AddDays(3)};
        Assert.Equal((HttpStatusCode)428,(await admin.PostAsJsonAsync($"/api/admin/quotations/{id}/issue",issueBody)).StatusCode);
        using var issue=new HttpRequestMessage(HttpMethod.Post,$"/api/admin/quotations/{id}/issue") {Content=JsonContent.Create(issueBody)};
        issue.Headers.Add("If-Match",created.Headers.ETag!.ToString());
        var issued=await admin.SendAsync(issue); var snapshot=await Body(issued);
        Assert.Equal("Issued",snapshot.GetProperty("status").GetString());Assert.Equal(110,snapshot.GetProperty("totalAmount").GetDecimal());
        using var stale=new HttpRequestMessage(HttpMethod.Post,$"/api/billing/quotations/{id}/accept");stale.Headers.Add("If-Match",created.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed,(await owner.SendAsync(stale)).StatusCode);
        using var accept=new HttpRequestMessage(HttpMethod.Post,$"/api/billing/quotations/{id}/accept");accept.Headers.Add("If-Match",issued.Headers.ETag!.ToString());
        var accepted=await owner.SendAsync(accept);Assert.Equal("Accepted",(await Body(accepted)).GetProperty("status").GetString());
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>database.Sql($"UPDATE quotations SET total_amount=999 WHERE id='{id}'"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>database.Sql($"UPDATE quotation_building_items SET unit_price=999 WHERE quotation_id='{id}'"));
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM payos_payment_requests"));
    }

    [BillingPostgresFact]
    public async Task Audit_failure_rolls_back_quotation_lines_and_receipt()
    {
        await using var database=await BillingDatabase.Create(); using var factory=new Factory(database);
        using var admin=factory.As(BillingDatabase.Admin); using var owner=factory.As(BillingDatabase.Owner);
        var package=await Package(admin);
        await database.Sql("ALTER TABLE audit_logs ADD CONSTRAINT reject_quote_audit CHECK(target_entity<>'quotations')");
        Assert.Equal(HttpStatusCode.InternalServerError,(await owner.SendAsync(QuoteRequest(package))).StatusCode);
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM quotations"));
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM quotation_building_items"));
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM billing_command_receipts"));
    }

    [BillingPostgresFact]
    public async Task Multi_building_discount_uses_best_rule_and_preserves_vnd_rounding()
    {
        await using var database=await BillingDatabase.Create();using var factory=new Factory(database);
        using var admin=factory.As(BillingDatabase.Admin);using var owner=factory.As(BillingDatabase.Owner);
        var second=Guid.NewGuid();var third=Guid.NewGuid();
        foreach(var building in new[]{second,third})await database.Sql($"INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{building}','{BillingDatabase.Org}','Extra',true,'{BillingDatabase.Owner}',now(),now()); INSERT INTO building_locations(building_id,address,created_at,updated_at) VALUES('{building}','Street',now(),now())");
        var packages=new[]{await Package(admin,"P100",100),await Package(admin,"P101",101),await Package(admin,"P103",103)};
        var from=DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Equal(HttpStatusCode.Created,(await admin.PostAsJsonAsync("/api/admin/discount-rules",new{code="FIXED",discountKind="Fixed",discountValue=100,minimumBuildings=3,validFrom=from})).StatusCode);
        var percent=await Body(await admin.PostAsJsonAsync("/api/admin/discount-rules",new{code="PERCENT",discountKind="Percent",discountValue=33.33,minimumBuildings=3,validFrom=from}));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/billing/quotations") {Content=JsonContent.Create(new{items=new[]{
            new{buildingId=BillingDatabase.Building,servicePackageId=packages[0],purchaseAction="New"},
            new{buildingId=second,servicePackageId=packages[1],purchaseAction="New"},new{buildingId=third,servicePackageId=packages[2],purchaseAction="New"}}})};
        request.Headers.Add("Idempotency-Key","multi");var quote=await Body(await owner.SendAsync(request));
        Assert.Equal(304,quote.GetProperty("subtotalAmount").GetDecimal());Assert.Equal(101,quote.GetProperty("discountAmount").GetDecimal());
        Assert.Equal(percent.GetProperty("id").GetGuid(),quote.GetProperty("discountRuleId").GetGuid());
        Assert.Equal(101,quote.GetProperty("items").EnumerateArray().Sum(x=>x.GetProperty("discountAmount").GetDecimal()));
        Assert.Equal(203,quote.GetProperty("items").EnumerateArray().Sum(x=>x.GetProperty("totalAmount").GetDecimal()));
    }

    [BillingPostgresFact]
    public async Task Enterprise_replay_is_scoped_and_does_not_create_payment_or_entitlement()
    {
        await using var database=await BillingDatabase.Create();using var factory=new Factory(database);using var owner=factory.As(BillingDatabase.Owner);
        using var other=factory.As(BillingDatabase.Other);
        for(var index=0;index<2;index++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/api/billing/enterprise-quote-requests")
            {Content=JsonContent.Create(new{requestedBuildingCount=150,requestedDurationMonths=12,contactName="Owner",contactEmail="owner@example.test"})};
            request.Headers.Add("Idempotency-Key","enterprise");Assert.Equal(HttpStatusCode.Created,(await owner.SendAsync(request)).StatusCode);
        }
        var list=await Body(await other.GetAsync("/api/billing/enterprise-quote-requests"));Assert.Equal(0,list.GetProperty("total").GetInt32());
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM enterprise_quote_requests"));
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM payos_payment_requests"));Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM service_entitlements"));
    }

    [BillingPostgresFact]
    public async Task Concurrent_accept_only_commits_once_and_catalog_edit_does_not_change_snapshot()
    {
        await using var database=await BillingDatabase.Create();using var factory=new Factory(database);
        using var admin=factory.As(BillingDatabase.Admin);using var owner=factory.As(BillingDatabase.Owner);
        var package=await Package(admin);var created=await owner.SendAsync(QuoteRequest(package));
        var id=(await Body(created)).GetProperty("id").GetGuid();
        using var issue=new HttpRequestMessage(HttpMethod.Post,$"/api/admin/quotations/{id}/issue")
        {Content=JsonContent.Create(new{taxAmount=0,terms="Frozen terms",validUntil=DateTimeOffset.UtcNow.AddDays(1)})};
        issue.Headers.Add("If-Match",created.Headers.ETag!.ToString());var issued=await admin.SendAsync(issue);await Body(issued);
        var current=await admin.GetAsync($"/api/billing/service-packages/{package}");
        using var edit=new HttpRequestMessage(HttpMethod.Patch,$"/api/admin/service-packages/{package}")
        {Content=JsonContent.Create(new{code="MONTH",name="New name",unitPrice=999,durationMonths=1})};
        edit.Headers.Add("If-Match",current.Headers.ETag!.ToString());Assert.Equal(HttpStatusCode.OK,(await admin.SendAsync(edit)).StatusCode);
        HttpRequestMessage Accept(){var r=new HttpRequestMessage(HttpMethod.Post,$"/api/billing/quotations/{id}/accept");r.Headers.Add("If-Match",issued.Headers.ETag!.ToString());return r;}
        var results=await Task.WhenAll(owner.SendAsync(Accept()),owner.SendAsync(Accept()));
        Assert.Single(results.Where(x=>x.StatusCode==HttpStatusCode.OK));Assert.Single(results.Where(x=>x.StatusCode==HttpStatusCode.PreconditionFailed));
        var final=await Body(await owner.GetAsync($"/api/billing/quotations/{id}"));Assert.Equal(100,final.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("Monthly Building",final.GetProperty("items")[0].GetProperty("packageName").GetString());
        Assert.Equal(1L,await database.Scalar($"SELECT count(*) FROM audit_logs WHERE target_id='{id}' AND new_values->>'status'='Accepted'"));
        var replay=await Body(await owner.SendAsync(QuoteRequest(package)));Assert.Equal("Draft",replay.GetProperty("status").GetString());Assert.Equal(100,replay.GetProperty("totalAmount").GetDecimal());
    }

    [BillingPostgresFact]
    public async Task Billing_requires_timezone_and_documents_required_headers()
    {
        await using var database=await BillingDatabase.Create();using var factory=new Factory(database);using var admin=factory.As(BillingDatabase.Admin);
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync("/api/admin/discount-rules",new{code="NOZONE",discountKind="Fixed",discountValue=1,minimumBuildings=1,validFrom="2026-10-01T00:00:00"})).StatusCode);
        using var anonymous=factory.CreateClient();var api=await Body(await anonymous.GetAsync("/openapi/v1.json"));
        var create=api.GetProperty("paths").GetProperty("/api/billing/quotations").GetProperty("post");
        var key=create.GetProperty("parameters").EnumerateArray().Single(x=>x.GetProperty("name").GetString()=="Idempotency-Key");Assert.True(key.GetProperty("required").GetBoolean());
        var issue=api.GetProperty("paths").GetProperty("/api/admin/quotations/{id}/issue").GetProperty("post");
        var etag=issue.GetProperty("parameters").EnumerateArray().Single(x=>x.GetProperty("name").GetString()=="If-Match");Assert.True(etag.GetProperty("required").GetBoolean());
        Assert.NotEmpty(create.GetProperty("security").EnumerateArray());
    }
}
