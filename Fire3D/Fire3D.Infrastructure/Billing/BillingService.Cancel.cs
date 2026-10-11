using Fire3D.Application.Billing;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Billing;

public sealed partial class BillingService
{
    private const string CancelOperation="CancelQuotation";

    /// <summary>
    /// Cancels Draft/Issued/Accepted quotations that have no valid payment and no unresolved checkout. Uses the same lock order
    /// as issue/accept/checkout (identity, actor, quotation row), so a checkout cannot be created for a quotation cancelled
    /// concurrently and a cancel cannot slip past a checkout that is still being created. Never cancels PayOS itself.
    /// </summary>
    public async Task<QuotationResponse> CancelQuotation(Guid actor,Guid family,Guid id,CancelQuotationRequest request,string? ifMatch,string? idempotencyKey,bool admin,CancellationToken ct)
    {
        var key=Key(idempotencyKey);
        Field(request.Reason is { } r && r.Trim().Length<=1000 && Text(r,1000),"reason","Supply a cancellation reason of 1-1,000 characters.");
        var reason=request.Reason!.Trim();
        var hash=Hash(new {id,reason});
        await using var tx=await db.Database.BeginTransactionAsync(ct);await IdentityLock(ct);
        var identity=await QuotationActor(actor,family,admin,ct);
        if(!admin && identity.Role!=UserRole.OrganizationUser)throw new BillingException(403,"ORGANIZATION_USER_REQUIRED","Use the admin route to cancel as PlatformAdmin.");
        // Scope is checked before any replay: another tenant gets 404, never a stored response.
        var quote=await Quote(identity,id,true,ct);
        var receipt=await Receipt(identity,CancelOperation,key,hash,ct);
        if(receipt is not null){var replay=Replay<QuotationResponse>(receipt);await tx.CommitAsync(ct);return replay;}
        if(quote.Status==QuotationStatus.Cancelled){var current=await QuoteView(quote,ct);await tx.CommitAsync(ct);return current;}
        BillingETag.Require(ifMatch,id,quote.Revision);
        if(quote.Status==QuotationStatus.Expired)throw new BillingException(409,"QUOTATION_EXPIRED","An expired quotation keeps its status.");
        if(await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM payos_payment_requests r WHERE r.quotation_id={id} AND (r.status='Paid'
              OR EXISTS(SELECT 1 FROM payment_transactions t WHERE t.payment_request_id=r.id AND t.status='Applied'))) AS "Value"
            """).SingleAsync(ct))
            throw new BillingException(409,"QUOTATION_ALREADY_PAID","A paid quotation cannot be cancelled; contact support for a refund or reconciliation.");
        // Creating/Ready/NeedsReconcile may still have a live provider link: resolve the checkout first (cancel or wait for expiry
        // confirmed by the provider). A local expiry time is not proof that the provider closed the link.
        var open=await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM billing_checkout_operations WHERE quotation_id={id} AND status IN('Creating','Ready','NeedsReconcile') ORDER BY created_at,id
            """).ToListAsync(ct);
        if(open.Count>0)throw new BillingException(409,"QUOTATION_CHECKOUT_UNRESOLVED","Cancel or reconcile the open checkout before cancelling the quotation.")
            {Extensions=new Dictionary<string,object?>{["checkoutIds"]=open}};
        var previous=quote.Status;
        quote.Status=QuotationStatus.Cancelled;quote.UpdatedAt=DateTime.UtcNow;quote.Revision++;
        Audit(identity,quote.OrganizationId,"quotations",id,AuditAction.Update,new {status=previous.ToString()},new {status="Cancelled",quote.Revision,reason});
        await db.SaveChangesAsync(ct);
        var response=await QuoteView(quote,ct);
        SaveReceipt(identity,CancelOperation,key,hash,id,response);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return response;
    }
}
