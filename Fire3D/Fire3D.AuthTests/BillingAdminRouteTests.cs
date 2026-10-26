using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>Admin catalog, draft, enterprise and AI quota routes of the billing API over HTTP, including their error codes.</summary>
public sealed class BillingAdminRouteTests
{
    private static async Task<JsonElement> Body(HttpResponseMessage response)
    { var text=await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode,text); return JsonDocument.Parse(text).RootElement.Clone(); }
    private static async Task<string?> Code(HttpResponseMessage response)
    { using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.TryGetProperty("code",out var code)?code.GetString():null; }
    private static HttpRequestMessage Send(HttpMethod method,string url,object? body,string? ifMatch=null,string? key=null)
    {
        var request=new HttpRequestMessage(method,url){Content=body is null?null:JsonContent.Create(body)};
        if(ifMatch is not null)request.Headers.TryAddWithoutValidation("If-Match",ifMatch);
        if(key is not null)request.Headers.Add("Idempotency-Key",key);
        return request;
    }
    private static async Task<Guid> Package(HttpClient admin,string code,decimal price=100)
    {
        var response=await admin.PostAsJsonAsync("/api/admin/service-packages",new {code,name="Package "+code,unitPrice=price,durationMonths=6,learnerLimit=10,aiQuotaUnits=0,isActive=true});
        return (await Body(response)).GetProperty("id").GetGuid();
    }

    [BillingPostgresFact]
    public async Task Quota_policy_and_discount_admin_routes_read_and_update_with_etags()
    {
        await using var database=await BillingDatabase.Create();using var factory=new BillingApiTests.Factory(database);
        using var admin=factory.As(BillingDatabase.Admin);using var owner=factory.As(BillingDatabase.Owner);
        var created=await admin.PostAsJsonAsync("/api/admin/billing/quota-policies",new{quotaUnit="tokens",effectiveFrom=DateTimeOffset.UtcNow.AddDays(-1)});
        Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var policy=(await Body(created)).GetProperty("id").GetGuid();
        Assert.Equal($"/api/admin/billing/quota-policies/{policy}",created.Headers.Location!.OriginalString);
        Assert.Equal("tokens",(await Body(await admin.GetAsync(created.Headers.Location))).GetProperty("quotaUnit").GetString());
        // A package may only grant units the AI service can spend (#176).
        var credit=await admin.PostAsJsonAsync("/api/admin/billing/quota-policies",new{quotaUnit="credit",effectiveFrom=DateTimeOffset.UtcNow,effectiveUntil=DateTimeOffset.UtcNow.AddDays(-1)});
        Assert.Equal((HttpStatusCode)422,credit.StatusCode);var creditBody=JsonDocument.Parse(await credit.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("AI_QUOTA_UNIT_UNSUPPORTED",creditBody.GetProperty("code").GetString());
        // Both findings come back together.
        Assert.True(creditBody.GetProperty("errors").TryGetProperty("quotaUnit",out _));Assert.True(creditBody.GetProperty("errors").TryGetProperty("effectiveUntil",out _));
        var expired=(await Body(await admin.PostAsJsonAsync("/api/admin/billing/quota-policies",new{quotaUnit="requests",effectiveFrom=DateTimeOffset.UtcNow.AddDays(-10),effectiveUntil=DateTimeOffset.UtcNow.AddDays(-1)}))).GetProperty("id").GetGuid();
        var page=await Body(await admin.GetAsync("/api/admin/billing/quota-policies?page=2&pageSize=1"));
        Assert.Equal(2,page.GetProperty("total").GetInt32());Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.GetAsync("/api/admin/billing/quota-policies?pageSize=101")).StatusCode);
        var missing=await admin.GetAsync($"/api/admin/billing/quota-policies/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);Assert.Equal("BILLING_RESOURCE_NOT_FOUND",await Code(missing));
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.GetAsync($"/api/admin/billing/quota-policies/{policy}")).StatusCode);
        // A quota-bearing package must point at a policy that is effective now.
        var stale=await admin.PostAsJsonAsync("/api/admin/service-packages",new{code="QUOTA",name="Quota",unitPrice=100,durationMonths=6,learnerLimit=10,aiQuotaUnits=50,aiPolicyVersionId=expired});
        Assert.Equal(HttpStatusCode.BadRequest,stale.StatusCode);Assert.Equal("BILLING_POLICY_INVALID",await Code(stale));
        Assert.Equal(0L,await database.Scalar("SELECT count(*) FROM service_packages"));

        var rule=await Body(await admin.PostAsJsonAsync("/api/admin/discount-rules",new{code="loyal",discountKind="Percent",discountValue=10,minimumBuildings=2,validFrom=DateTimeOffset.UtcNow.AddMinutes(-1)}));
        var id=rule.GetProperty("id").GetGuid();
        Assert.Equal("LOYAL",Assert.Single((await Body(await admin.GetAsync("/api/admin/discount-rules"))).EnumerateArray()).GetProperty("code").GetString());
        var detail=await admin.GetAsync($"/api/admin/discount-rules/{id}");
        Assert.Equal(1,(await Body(detail)).GetProperty("revision").GetInt64());Assert.Equal(BillingETag.Format(id,1),detail.Headers.ETag!.ToString());
        var unknown=await admin.GetAsync($"/api/admin/discount-rules/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound,unknown.StatusCode);Assert.Equal("BILLING_RESOURCE_NOT_FOUND",await Code(unknown));
        var body=new{code="loyal",discountKind="Percent",discountValue=15,minimumBuildings=2,validFrom=DateTimeOffset.UtcNow.AddMinutes(-1)};
        Assert.Equal((HttpStatusCode)428,(await admin.SendAsync(Send(HttpMethod.Patch,$"/api/admin/discount-rules/{id}",body))).StatusCode);
        var malformed=await admin.SendAsync(Send(HttpMethod.Patch,$"/api/admin/discount-rules/{id}",body,"\"7\""));
        Assert.Equal(HttpStatusCode.BadRequest,malformed.StatusCode);Assert.Equal("INVALID_IF_MATCH",await Code(malformed));
        Assert.Equal(HttpStatusCode.PreconditionFailed,(await admin.SendAsync(Send(HttpMethod.Patch,$"/api/admin/discount-rules/{id}",body,BillingETag.Format(id,9)))).StatusCode);
        var updated=await admin.SendAsync(Send(HttpMethod.Patch,$"/api/admin/discount-rules/{id}",body,detail.Headers.ETag!.ToString()));
        Assert.Equal(15,(await Body(updated)).GetProperty("discountValue").GetDecimal());
        Assert.Equal(BillingETag.Format(id,2),updated.Headers.ETag!.ToString());
        Assert.Equal(1L,await database.Scalar($"SELECT count(*) FROM audit_logs WHERE target_id='{id}' AND action='Update'"));
    }

    [BillingPostgresFact]
    public async Task Draft_update_and_list_follow_scope_and_issue_rejects_an_unsupported_interval()
    {
        await using var database=await BillingDatabase.Create();using var factory=new BillingApiTests.Factory(database);
        using var admin=factory.As(BillingDatabase.Admin);using var owner=factory.As(BillingDatabase.Owner);using var other=factory.As(BillingDatabase.Other);
        var first=await Package(admin,"FIRST");var second=await Package(admin,"SECOND",200);
        var created=await owner.SendAsync(Send(HttpMethod.Post,"/api/billing/quotations",new{items=new[]{new{buildingId=BillingDatabase.Building,servicePackageId=first,purchaseAction="New"}}},key:"draft"));
        var id=(await Body(created)).GetProperty("id").GetGuid();
        var items=new{items=new[]{new{buildingId=BillingDatabase.Building,servicePackageId=second,purchaseAction="New"}}};
        Assert.Equal(HttpStatusCode.PreconditionFailed,(await owner.SendAsync(Send(HttpMethod.Patch,$"/api/billing/quotations/{id}",items,BillingETag.Format(id,5)))).StatusCode);
        var updated=await owner.SendAsync(Send(HttpMethod.Patch,$"/api/billing/quotations/{id}",items,created.Headers.ETag!.ToString()));
        var draft=await Body(updated);
        Assert.Equal(1200,draft.GetProperty("totalAmount").GetDecimal());Assert.Equal(second,draft.GetProperty("items")[0].GetProperty("servicePackageId").GetGuid());
        Assert.Equal(BillingETag.Format(id,2),updated.Headers.ETag!.ToString());
        // A draft keeps its purpose.
        var purpose=await owner.SendAsync(Send(HttpMethod.Patch,$"/api/billing/quotations/{id}",new{purpose="AIQuotaTopUp"},updated.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.Conflict,purpose.StatusCode);Assert.Equal("BILLING_STATE_CONFLICT",await Code(purpose));
        Assert.Equal(HttpStatusCode.NotFound,(await other.SendAsync(Send(HttpMethod.Patch,$"/api/billing/quotations/{id}",items,updated.Headers.ETag!.ToString()))).StatusCode);

        var topUp=await owner.SendAsync(Send(HttpMethod.Post,"/api/billing/quotations",new{purpose="AIQuotaTopUp",topUp=new{requestedQuotaUnits=100}},key:"topup"));
        var topUpId=(await Body(topUp)).GetProperty("id").GetGuid();
        var raised=await Body(await owner.SendAsync(Send(HttpMethod.Patch,$"/api/billing/quotations/{topUpId}",new{purpose="AIQuotaTopUp",topUp=new{requestedQuotaUnits=250}},topUp.Headers.ETag!.ToString())));
        Assert.Equal(250,raised.GetProperty("topUp").GetProperty("requestedQuotaUnits").GetInt32());
        Assert.Equal(250,(int)(await database.Scalar($"SELECT requested_quota_units FROM quotation_topup_items WHERE quotation_id='{topUpId}'"))!);

        var mine=await Body(await owner.GetAsync("/api/billing/quotations?page=1&pageSize=1"));
        Assert.Equal(2,mine.GetProperty("total").GetInt32());Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal(0,(await Body(await other.GetAsync("/api/billing/quotations"))).GetProperty("total").GetInt32());
        Assert.Equal(2,(await Body(await admin.GetAsync("/api/billing/quotations"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.GetAsync("/api/billing/quotations?pageSize=0")).StatusCode);

        // A start whose term end overflows the calendar is a validation error, not a server error.
        var line=draft.GetProperty("items")[0].GetProperty("id").GetGuid();
        var overflow=await admin.SendAsync(Send(HttpMethod.Post,$"/api/admin/quotations/{id}/issue",new{taxAmount=0,terms="Terms",validUntil=DateTimeOffset.UtcNow.AddDays(1),
            items=new[]{new{quotationItemId=line,startsAt=new DateTimeOffset(9999,10,1,0,0,0,TimeSpan.Zero)}}},updated.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.BadRequest,overflow.StatusCode);Assert.Equal("BILLING_VALIDATION_FAILED",await Code(overflow));
        var issued=await admin.SendAsync(Send(HttpMethod.Post,$"/api/admin/quotations/{id}/issue",new{taxAmount=0,terms="Terms",validUntil=DateTimeOffset.UtcNow.AddDays(1),
            items=new[]{new{quotationItemId=line,startsAt=DateTimeOffset.UtcNow.AddDays(2)}}},updated.Headers.ETag!.ToString()));
        Assert.Equal("Issued",(await Body(issued)).GetProperty("status").GetString());
        var locked=await owner.SendAsync(Send(HttpMethod.Patch,$"/api/billing/quotations/{id}",items,issued.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.Conflict,locked.StatusCode);Assert.Equal("BILLING_STATE_CONFLICT",await Code(locked));
    }

    [BillingPostgresFact]
    public async Task Enterprise_admin_routes_list_read_update_reply_and_quote_for_an_active_requester_only()
    {
        await using var database=await BillingDatabase.Create();using var factory=new BillingApiTests.Factory(database);
        using var admin=factory.As(BillingDatabase.Admin);using var owner=factory.As(BillingDatabase.Owner);
        var package=await Package(admin,"ENT");
        async Task<Guid> Request(string key)=>(await Body(await owner.SendAsync(Send(HttpMethod.Post,"/api/billing/enterprise-quote-requests",
            new{requestedBuildingCount=150,requestedDurationMonths=12,contactName="Owner",contactEmail="owner@example.test"},key:key)))).GetProperty("id").GetGuid();
        var id=await Request("ent-1");
        var list=await Body(await admin.GetAsync("/api/admin/enterprise-quote-requests?pageSize=5"));
        Assert.Equal(1,list.GetProperty("total").GetInt32());Assert.Equal(id,list.GetProperty("items")[0].GetProperty("id").GetGuid());
        var detail=await admin.GetAsync($"/api/admin/enterprise-quote-requests/{id}");
        Assert.Equal("New",(await Body(detail)).GetProperty("status").GetString());Assert.Equal(BillingETag.Format(id,1),detail.Headers.ETag!.ToString());
        var contacted=await admin.SendAsync(Send(HttpMethod.Patch,$"/api/admin/enterprise-quote-requests/{id}",new{status="Contacted",assignedTo=BillingDatabase.Admin},detail.Headers.ETag!.ToString()));
        var contactedBody=await Body(contacted);
        Assert.Equal("Contacted",contactedBody.GetProperty("status").GetString());Assert.Equal(BillingDatabase.Admin,contactedBody.GetProperty("assignedTo").GetGuid());
        Assert.Equal(BillingETag.Format(id,2),contacted.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Created,(await admin.SendAsync(Send(HttpMethod.Post,$"/api/admin/enterprise-quote-requests/{id}/responses",new{message="We will call you."},key:"reply"))).StatusCode);
        var replies=await Body(await admin.GetAsync($"/api/admin/enterprise-quote-requests/{id}/responses"));
        Assert.Equal("We will call you.",replies.GetProperty("items")[0].GetProperty("message").GetString());

        var lines=new{items=new[]{new{buildingId=BillingDatabase.Building,servicePackageId=package,purchaseAction="New"}}};
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.SendAsync(Send(HttpMethod.Post,$"/api/admin/enterprise-quote-requests/{id}/quotations",lines))).StatusCode);
        var quoted=await admin.SendAsync(Send(HttpMethod.Post,$"/api/admin/enterprise-quote-requests/{id}/quotations",lines,key:"ent-quote"));
        Assert.Equal(HttpStatusCode.Created,quoted.StatusCode);
        var quote=await Body(quoted);var quoteId=quote.GetProperty("id").GetGuid();
        Assert.Equal($"/api/billing/quotations/{quoteId}",quoted.Headers.Location!.OriginalString);
        Assert.Equal(BillingETag.Format(quoteId,quote.GetProperty("revision").GetInt64()),quoted.Headers.ETag!.ToString());
        Assert.Equal("Draft",(await Body(await owner.GetAsync(quoted.Headers.Location))).GetProperty("status").GetString());
        Assert.Equal("Quoted",(await Body(await admin.GetAsync($"/api/admin/enterprise-quote-requests/{id}"))).GetProperty("status").GetString());

        // The draft is created for the requesting OrganizationUser; a deactivated requester cannot receive one.
        var second=await Request("ent-2");
        await database.Sql($"UPDATE users SET is_active=false WHERE id='{BillingDatabase.Owner}'");
        var refused=await admin.SendAsync(Send(HttpMethod.Post,$"/api/admin/enterprise-quote-requests/{second}/quotations",lines,key:"ent-quote-2"));
        Assert.Equal(HttpStatusCode.Conflict,refused.StatusCode);Assert.Equal("ENTERPRISE_REQUESTER_UNAVAILABLE",await Code(refused));
        Assert.Equal(1L,await database.Scalar("SELECT count(*) FROM quotations"));
        Assert.Equal("New",(string?)await database.Scalar($"SELECT status FROM enterprise_quote_requests WHERE id='{second}'"));
    }

    [BillingPostgresFact]
    public async Task Admin_reads_organization_ai_quota_grants_and_usage_and_unavailable_sessions_or_organizations_are_refused()
    {
        await using var database=await BillingDatabase.Create();using var factory=new BillingApiTests.Factory(database);
        using var admin=factory.As(BillingDatabase.Admin);using var owner=factory.As(BillingDatabase.Owner);
        // Grant provenance (payment + top-up line) is covered by the top-up tests; here only the ledger row matters.
        await database.Sql($"""
            SET session_replication_role=replica;
            INSERT INTO billing_ai_quota_grants(id,organization_id,policy_version_id,quota_unit,quota_units,starts_at,ends_at,payment_transaction_id,provisioning_key,source_kind,topup_item_id)
            VALUES(gen_random_uuid(),'{BillingDatabase.Org}',gen_random_uuid(),'tokens',500,now()-interval '1 hour',now()+interval '30 days',gen_random_uuid(),'fixture:'||gen_random_uuid(),'TopUp',gen_random_uuid());
            SET session_replication_role=origin;
            """);
        var grants=await Body(await admin.GetAsync($"/api/admin/organizations/{BillingDatabase.Org}/ai-quota/grants?pageSize=10"));
        Assert.Equal(1,grants.GetProperty("total").GetInt32());Assert.Equal(500,grants.GetProperty("items")[0].GetProperty("quotaUnits").GetInt32());
        Assert.Equal(0,(await Body(await admin.GetAsync($"/api/admin/organizations/{BillingDatabase.OtherOrg}/ai-quota/grants"))).GetProperty("total").GetInt32());
        var usage=await Body(await admin.GetAsync($"/api/admin/organizations/{BillingDatabase.Org}/ai-usage"));
        Assert.Equal(0,usage.GetProperty("total").GetInt32());Assert.Equal(20,usage.GetProperty("pageSize").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound,(await admin.GetAsync($"/api/admin/organizations/{Guid.NewGuid()}/ai-usage")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.GetAsync($"/api/admin/organizations/{BillingDatabase.Org}/ai-quota/grants?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.GetAsync($"/api/admin/organizations/{BillingDatabase.Org}/ai-usage")).StatusCode);

        await database.Sql($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{BillingDatabase.Owner}'");
        var revoked=await owner.GetAsync("/api/organizations/me/notifications");
        Assert.Equal(HttpStatusCode.Unauthorized,revoked.StatusCode);Assert.Equal("SESSION_REVOKED",await Code(revoked));
        await database.Sql($"UPDATE organizations SET is_active=false WHERE id='{BillingDatabase.Org}'");
        var inactive=await owner.GetAsync("/api/billing/service-packages");
        Assert.Equal(HttpStatusCode.Forbidden,inactive.StatusCode);Assert.Equal("ORGANIZATION_UNAVAILABLE",await Code(inactive));
    }
}
