using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Billing;

public sealed partial class BillingService
{
    private sealed record EnterpriseResponseRow(Guid Id,Guid RequestId,Guid AuthorId,string? AuthorName,string Message,DateTime CreatedAt);
    private async Task<EnterpriseQuoteRequest> LockedEnterprise(Actor identity,Guid id,CancellationToken ct)
    {
        await Lock("fet3d:billing:enterprise:"+id,false,ct);
        var item=await db.Set<EnterpriseQuoteRequest>().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
        await Scope(identity,item.OrganizationId,ct);return item;
    }

    /// <summary>Admin reply to the requesting organization. Append-only; Idempotency-Key; not allowed once Closed.</summary>
    public async Task<EnterpriseResponseItem> AddEnterpriseResponse(Guid actor,Guid family,Guid id,EnterpriseResponseBody request,string? key,CancellationToken ct)
    {
        Field(Text(request.Message,10000),"message","Supply a message of 1-10,000 characters.");
        var message=request.Message!.Trim();var stableKey=Key(key);var hash=Hash(new {id,message});
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,true,ct);
        var item=await LockedEnterprise(identity,id,ct);
        var previous=await Receipt(identity,"EnterpriseResponse",stableKey,hash,ct);
        if(previous is not null){var replay=Replay<EnterpriseResponseItem>(previous);await tx.CommitAsync(ct);return replay;}
        if(item.Status=="Closed")throw new BillingException(409,"ENTERPRISE_REQUEST_CLOSED","A closed enterprise request accepts no further responses.");
        var responseId=Guid.NewGuid();var now=DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO enterprise_quote_responses(id,request_id,author_id,message,created_at) VALUES({responseId},{id},{actor},{message},{now})",ct);
        var row=await db.Database.SqlQuery<EnterpriseResponseRow>($"""
            SELECT r.id AS "Id",r.request_id AS "RequestId",r.author_id AS "AuthorId",COALESCE(NULLIF(btrim(u.full_name),''),u.username) AS "AuthorName",r.message AS "Message",r.created_at AS "CreatedAt"
            FROM enterprise_quote_responses r LEFT JOIN users u ON u.id=r.author_id AND u.deleted_at IS NULL WHERE r.id={responseId}
            """).SingleAsync(ct);
        var response=new EnterpriseResponseItem(row.Id,row.RequestId,row.AuthorId,row.AuthorName,row.Message,row.CreatedAt);
        SaveReceipt(identity,"EnterpriseResponse",stableKey,hash,id,response);
        // The message text stays in the response table, not in the audit row.
        Audit(identity,item.OrganizationId,"enterprise_quote_requests",id,AuditAction.Update,null,new {operation="Respond",responseId});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }

    public async Task<BillingPage<EnterpriseResponseItem>> ListEnterpriseResponses(Guid actor,Guid family,Guid id,int page,int pageSize,CancellationToken ct)
    {
        Pagination(page,pageSize);await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);var identity=await Authorize(actor,false,ct);await RequireLiveFamily(actor,family,ct);
        var item=await db.Set<EnterpriseQuoteRequest>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
        await Scope(identity,item.OrganizationId,ct);
        var total=await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM enterprise_quote_responses WHERE request_id={id}""").SingleAsync(ct);
        var skip=(long)(page-1)*pageSize;
        var rows=await db.Database.SqlQuery<EnterpriseResponseRow>($"""
            SELECT r.id AS "Id",r.request_id AS "RequestId",r.author_id AS "AuthorId",COALESCE(NULLIF(btrim(u.full_name),''),u.username) AS "AuthorName",r.message AS "Message",r.created_at AS "CreatedAt"
            FROM enterprise_quote_responses r LEFT JOIN users u ON u.id=r.author_id AND u.deleted_at IS NULL WHERE r.request_id={id}
            ORDER BY r.created_at,r.id OFFSET {skip} LIMIT {pageSize}
            """).ToListAsync(ct);
        return new(rows.Select(x=>new EnterpriseResponseItem(x.Id,x.RequestId,x.AuthorId,x.AuthorName,x.Message,x.CreatedAt)).ToArray(),total,page,pageSize);
    }

    /// <summary>Ends handling with If-Match, Idempotency-Key and a reason. A quotation or payment linked to the request is not cancelled and no service is granted.</summary>
    public async Task<EnterpriseQuoteResponse> CloseEnterpriseRequest(Guid actor,Guid family,Guid id,EnterpriseCloseRequest request,string? ifMatch,string? key,CancellationToken ct)
    {
        Field(Text(request.Reason,1000),"reason","Supply a close reason of 1-1,000 characters.");
        var reason=request.Reason!.Trim();var stableKey=Key(key);var hash=Hash(new {id,reason});
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);var identity=await QuotationActor(actor,family,true,ct);
        var item=await LockedEnterprise(identity,id,ct);
        var previous=await Receipt(identity,"CloseEnterpriseRequest",stableKey,hash,ct);
        if(previous is not null){var replay=Replay<EnterpriseQuoteResponse>(previous);await tx.CommitAsync(ct);return replay;}
        if(item.Status=="Closed"){var current=EnterpriseView(item);await tx.CommitAsync(ct);return current;}
        BillingETag.Require(ifMatch,id,item.Revision);
        var old=new{item.Status,item.Revision};
        item.Status="Closed";item.ClosedAt=DateTime.UtcNow;item.ClosedBy=actor;item.CloseReason=reason;item.Revision++;item.UpdatedAt=DateTime.UtcNow;
        Audit(identity,item.OrganizationId,"enterprise_quote_requests",id,AuditAction.Update,old,new{item.Status,item.Revision,reason});
        var response=EnterpriseView(item);SaveReceipt(identity,"CloseEnterpriseRequest",stableKey,hash,id,response);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }
}
