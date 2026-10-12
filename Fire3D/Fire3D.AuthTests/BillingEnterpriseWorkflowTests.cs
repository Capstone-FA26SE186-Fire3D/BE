using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingEnterpriseWorkflowTests
{
    [BillingPostgresFact]
    public async Task Assignment_responses_and_close_follow_roles_etags_and_leave_quotations_untouched()
    {
        await using var db=await BillingDatabase.Create();await using var context=db.Context();var billing=new BillingService(context);
        var request=await billing.CreateEnterpriseRequest(BillingDatabase.Owner,BillingDatabase.Owner,new(5,12,"Contact","contact@example.test"),"ent",default);
        // Assignment keeps the status; omitted fields keep their value; only an active PlatformAdmin can be assigned.
        var assigned=await billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new(AssignedTo:BillingDatabase.Admin),BillingETag.Format(request.Id,1),default);
        Assert.Equal("New",assigned.Status);Assert.Equal(BillingDatabase.Admin,assigned.AssignedTo);Assert.Equal(2,assigned.Revision);
        // The organization sees its request but not the internal assignee.
        Assert.Null((await billing.GetEnterpriseRequest(BillingDatabase.Owner,BillingDatabase.Owner,request.Id,default)).AssignedTo);
        Assert.Equal(400,(await Assert.ThrowsAsync<BillingException>(()=>billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new(AssignedTo:BillingDatabase.Owner),BillingETag.Format(request.Id,2),default))).Status);
        Assert.Equal(412,(await Assert.ThrowsAsync<BillingException>(()=>billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Contacted"),BillingETag.Format(request.Id,1),default))).Status);
        var contacted=await billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Contacted"),BillingETag.Format(request.Id,2),default);
        Assert.Equal(BillingDatabase.Admin,contacted.AssignedTo);Assert.Equal("Contacted",contacted.Status);
        var cleared=await billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new(ClearAssignment:true),BillingETag.Format(request.Id,3),default);
        Assert.Null(cleared.AssignedTo);Assert.Equal("Contacted",cleared.Status);

        // Responses are append-only, replayable by key and readable by the requesting organization only.
        var reply=await billing.AddEnterpriseResponse(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("We will call you tomorrow."),"reply-1",default);
        Assert.Equal(reply.Id,(await billing.AddEnterpriseResponse(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("We will call you tomorrow."),"reply-1",default)).Id);
        Assert.Equal(409,(await Assert.ThrowsAsync<BillingException>(()=>billing.AddEnterpriseResponse(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Other text"),"reply-1",default))).Status);
        Assert.Equal(403,(await Assert.ThrowsAsync<BillingException>(()=>billing.AddEnterpriseResponse(BillingDatabase.Owner,BillingDatabase.Owner,request.Id,new("Self reply"),"reply-owner",default))).Status);
        Assert.Equal(1,(await billing.ListEnterpriseResponses(BillingDatabase.Owner,BillingDatabase.Owner,request.Id,1,20,default)).Total);
        Assert.Equal(404,(await Assert.ThrowsAsync<BillingException>(()=>billing.ListEnterpriseResponses(BillingDatabase.Other,BillingDatabase.Other,request.Id,1,20,default))).Status);
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Sql($"UPDATE enterprise_quote_responses SET message='edited' WHERE id='{reply.Id}'"));
        Assert.DoesNotContain("call you",(string)(await db.Scalar($"SELECT string_agg(new_values::text,';') FROM audit_logs WHERE target_id='{request.Id}'"))!);

        // A quotation linked to the request survives closing; closing replays and needs a reason.
        var package=await billing.SavePackage(BillingDatabase.Admin,BillingDatabase.Admin,null,new("ENT","Enterprise",1000,12,true,null,100,0),null,default);
        var quote=await billing.CreateEnterpriseQuotation(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new([new(BillingDatabase.Building,package.Id,"New")]),"ent-quote",default);
        var quoted=await billing.GetEnterpriseRequest(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,default);
        Assert.Equal(400,(await Assert.ThrowsAsync<BillingException>(()=>billing.CloseEnterpriseRequest(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("  "),BillingETag.Format(request.Id,quoted.Revision),"close",default))).Status);
        Assert.Equal(403,(await Assert.ThrowsAsync<BillingException>(()=>billing.CloseEnterpriseRequest(BillingDatabase.Owner,BillingDatabase.Owner,request.Id,new("done"),BillingETag.Format(request.Id,quoted.Revision),"close-owner",default))).Status);
        var closed=await billing.CloseEnterpriseRequest(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Customer signed elsewhere"),BillingETag.Format(request.Id,quoted.Revision),"close",default);
        Assert.Equal("Closed",closed.Status);Assert.Equal("Customer signed elsewhere",closed.CloseReason);Assert.Equal(quote.Id,closed.QuotationId);
        var ownerView=await billing.GetEnterpriseRequest(BillingDatabase.Owner,BillingDatabase.Owner,request.Id,default);
        Assert.Equal("Closed",ownerView.Status);Assert.Null(ownerView.CloseReason);Assert.Null(ownerView.AssignedTo);
        await db.Sql($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{BillingDatabase.Owner}'");
        Assert.Equal(401,(await Assert.ThrowsAsync<BillingException>(()=>billing.GetEnterpriseRequest(BillingDatabase.Owner,BillingDatabase.Owner,request.Id,default))).Status);
        Assert.Equal(401,(await Assert.ThrowsAsync<BillingException>(()=>billing.ListEnterpriseResponses(BillingDatabase.Owner,BillingDatabase.Owner,request.Id,1,20,default))).Status);
        Assert.Equal(closed.Revision,(await billing.CloseEnterpriseRequest(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Customer signed elsewhere"),BillingETag.Format(request.Id,quoted.Revision),"close",default)).Revision);
        Assert.Equal("Draft",(string?)await db.Scalar($"SELECT status::text FROM quotations WHERE id='{quote.Id}'"));
        var closeAudit=(Guid)(await db.Scalar($"SELECT id FROM audit_logs WHERE target_entity='enterprise_quote_requests' AND target_id='{request.Id}' AND new_values->>'Status'='Closed'"))!;
        Assert.Equal("Customer signed elsewhere",(await new Fire3D.Infrastructure.Reporting.AuditQueries(context).Get(BillingDatabase.Admin,BillingDatabase.Admin,closeAudit,default)).Value!.Reason);
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM service_entitlements"));
        Assert.Equal(409,(await Assert.ThrowsAsync<BillingException>(()=>billing.AddEnterpriseResponse(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Late"),"reply-late",default))).Status);
        Assert.Equal(409,(await Assert.ThrowsAsync<BillingException>(()=>billing.UpdateEnterpriseStatus(BillingDatabase.Admin,BillingDatabase.Admin,request.Id,new("Rejected"),BillingETag.Format(request.Id,closed.Revision),default))).Status);
    }

    [BillingPostgresFact]
    public async Task Organization_reads_its_enterprise_request_and_replies_over_HTTP()
    {
        await using var db=await BillingDatabase.Create();using var factory=new BillingApiTests.Factory(db);
        using var owner=factory.As(BillingDatabase.Owner);using var other=factory.As(BillingDatabase.Other);using var admin=factory.As(BillingDatabase.Admin);
        var create=new HttpRequestMessage(HttpMethod.Post,"/api/billing/enterprise-quote-requests"){Content=JsonContent.Create(new {requestedBuildingCount=3,contactName="Lan",contactEmail="lan@example.test"})};
        create.Headers.Add("Idempotency-Key","ent-http");
        var id=(await PayosCheckoutTests.Json(await owner.SendAsync(create))).GetProperty("id").GetGuid();
        var respond=new HttpRequestMessage(HttpMethod.Post,$"/api/admin/enterprise-quote-requests/{id}/responses"){Content=JsonContent.Create(new {message="Thanks, we received it."})};
        respond.Headers.Add("Idempotency-Key","ent-reply");
        Assert.Equal(HttpStatusCode.Created,(await admin.SendAsync(respond)).StatusCode);
        Assert.Equal("New",(await PayosCheckoutTests.Json(await owner.GetAsync($"/api/billing/enterprise-quote-requests/{id}"))).GetProperty("status").GetString());
        Assert.Equal("Thanks, we received it.",(await PayosCheckoutTests.Json(await owner.GetAsync($"/api/billing/enterprise-quote-requests/{id}/responses"))).GetProperty("items")[0].GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/billing/enterprise-quote-requests/{id}/responses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.PostAsJsonAsync($"/api/admin/enterprise-quote-requests/{id}/close",new {reason="x"})).StatusCode);
        var close=new HttpRequestMessage(HttpMethod.Post,$"/api/admin/enterprise-quote-requests/{id}/close"){Content=JsonContent.Create(new {reason="Handled"})};
        close.Headers.Add("Idempotency-Key","ent-close");
        Assert.Equal((HttpStatusCode)428,(await admin.SendAsync(close)).StatusCode);
    }
}
