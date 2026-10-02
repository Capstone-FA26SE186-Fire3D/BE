using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.Infrastructure.Billing;
public sealed partial class PayosPayments
{
    public async Task<CheckoutResult> Cancel(Guid actorId,Guid family,Guid id,string? key,CancellationToken ct)
    {
        var value=Key(key);var hash=Hash(new{id});BillingCheckoutOperation op;bool claimed;
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            await Identity(ct);var actor=await ActorFor(actorId,true,ct);await Family(actorId,family,ct);
            var request=await db.PayosPaymentRequests.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();Scope(actor,request.OrganizationId);
            await Lock($"fet3d:payos:cancel:{actorId}:{value}",ct);
            var receipt=await db.Set<BillingCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x=>x.ActorId==actorId&&x.Operation=="PayosCancel"&&x.IdempotencyKey==value,ct);
            if(receipt is not null&&receipt.InputHash!=hash)throw Conflict("IDEMPOTENCY_KEY_CONFLICT","The key was used for different input.");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM billing_checkout_operations WHERE payment_request_id={id} FOR UPDATE",ct);
            op=await db.Set<BillingCheckoutOperation>().SingleAsync(x=>x.PaymentRequestId==id,ct);
            if(receipt is not null)
            {
                // Replay observes the committed operation even if a late payment won cancellation.
                // The worker owns retries; repeat requests must not re-call the provider or alter leases.
                await tx.CommitAsync(ct);
                return new(View(op),op.Status is "Cancelled" or "Expired" or "Completed" or "Failed"?200:202);
            }
            if(op.Status=="Completed")throw Conflict("PAYMENT_ALREADY_PAID","A paid payment cannot be cancelled.");
            if(receipt is null)
            {
                db.Add(new BillingCommandReceipt{Id=Guid.NewGuid(),ActorId=actorId,Operation="PayosCancel",IdempotencyKey=value,InputHash=hash,ResourceId=op.Id,CreatedAt=Now});
                Audit(actorId,request.OrganizationId,"CheckoutCancelRequested",op.Id);
            }
            claimed=op.Status is not ("Cancelled" or "Expired")&&(op.LeaseUntil is null||op.LeaseUntil<=Now);
            op.CancelRequested=true;
            if(claimed){op.LeaseToken=Guid.NewGuid();op.LeaseUntil=Now.AddSeconds(60);op.Attempts++;}
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();if(claimed)await RunCheckout(op,false,ct);
        op=await db.Set<BillingCheckoutOperation>().AsNoTracking().SingleAsync(x=>x.Id==op.Id,ct);
        return new(View(op),op.Status is "Cancelled" or "Expired"?200:202);
    }
}
