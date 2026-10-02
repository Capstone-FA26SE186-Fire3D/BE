using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Fire3D.Infrastructure.Billing;
public sealed partial class PayosPayments(Fire3DDbContext db,IPayosProvider provider,PayosExecutor executor,
    IOptions<PayosOptions> options,TimeProvider clock,ILogger<PayosPayments> logger) : IPayosPayments
{
    private DateTime Now=>clock.GetUtcNow().UtcDateTime;
    private sealed record Actor(Guid Id,UserRole Role,Guid? OrganizationId);
    private static BillingException Missing()=>new(404,"BILLING_RESOURCE_NOT_FOUND","The billing resource was not found in your scope.");
    private static BillingException Conflict(string code,string message)=>new(409,code,message);
    private static string Hash<T>(T input)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
    private static string Key(string? key)
    {
        if(key is null||key.Length is <1 or >128||key.Any(c=>c<'!'||c>'~'))throw new BillingException(400,"IDEMPOTENCY_KEY_REQUIRED","Supply 1–128 printable ASCII characters without spaces.",new(){{"idempotencyKey",["Idempotency-Key is required."]}});
        return key;
    }
    private async Task Lock(string resource,CancellationToken ct)=>await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({resource},0))",ct);
    private async Task Identity(CancellationToken ct)=>await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0))",ct);
    private async Task<Actor> ActorFor(Guid id,bool organizationOnly,CancellationToken ct)
    {
        var actor=await db.Users.AsNoTracking().Where(x=>x.Id==id&&x.IsActive&&x.DeletedAt==null)
            .Select(x=>new Actor(x.Id,x.Role,x.OrganizationId)).SingleOrDefaultAsync(ct)??throw new BillingException(401,"ACCOUNT_UNAVAILABLE","The account is no longer active.");
        if(actor.Role is not (UserRole.PlatformAdmin or UserRole.OrganizationUser)||organizationOnly&&actor.Role!=UserRole.OrganizationUser)
            throw new BillingException(403,"BILLING_ROLE_FORBIDDEN","This operation is not allowed for your role.");
        if(actor.Role==UserRole.OrganizationUser&&!await db.Organizations.AnyAsync(x=>x.Id==actor.OrganizationId&&x.IsActive&&x.DeletedAt==null,ct))
            throw new BillingException(403,"ORGANIZATION_UNAVAILABLE","The organization is unavailable.");
        return actor;
    }
    private async Task Family(Guid actor,Guid family,CancellationToken ct)
    {
        if(!await db.Set<RefreshToken>().AnyAsync(x=>x.UserId==actor&&x.FamilyId==family&&x.RevokedAt==null&&x.ConsumedAt==null&&x.ExpiresAt>Now,ct))
            throw new BillingException(401,"SESSION_REVOKED","The session is no longer active.");
    }
    private static void Scope(Actor actor,Guid organization){if(actor.Role!=UserRole.PlatformAdmin&&actor.OrganizationId!=organization)throw Missing();}
    private void Audit(Guid actor,Guid? org,string action,Guid id)=>db.AuditLogs.Add(new AuditLog
    {Id=Guid.NewGuid(),UserId=actor,OrganizationId=org,ActorType="User",Action=AuditAction.Payment,TargetEntity="billing_checkout_operations",
        TargetId=id,CorrelationId=id,NewValues=JsonSerializer.Serialize(new{operation=action}),CreatedAt=Now});
    private static PayosCreateInput Input(BillingCheckoutOperation op)=>JsonSerializer.Deserialize<PayosCreateInput>(op.ProviderInput)
        ??throw new InvalidOperationException("Missing persisted provider input.");
    private static PayosLink? Result(BillingCheckoutOperation op)=>op.ProviderResult is null?null:JsonSerializer.Deserialize<PayosLink>(op.ProviderResult);
    private static PayosCheckoutResponse View(BillingCheckoutOperation op)
    {
        var input=Input(op);var link=Result(op);
        return new(op.Id,op.QuotationId,op.PaymentRequestId,op.OrderCode,input.Amount,"VND",op.Status,
            op.PaymentRequestId.HasValue?link?.CheckoutUrl:null,op.PaymentRequestId.HasValue?link?.QrCode:null,input.ExpiresAt,op.LastError);
    }
    public async Task<CheckoutResult> Create(Guid actorId,Guid family,CreatePayosRequest request,string? key,CancellationToken ct)
    {
        if(!options.Value.Enabled)throw new BillingException(503,"PAYOS_DISABLED","Payment checkout is disabled.");
        var idempotency=Key(key);var hash=Hash(request);BillingCheckoutOperation op;
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            await Identity(ct);var actor=await ActorFor(actorId,true,ct);await Family(actorId,family,ct);
            await Lock($"fet3d:payos:create:{actorId}:{idempotency}",ct);
            var receipt=await db.Set<BillingCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x=>x.ActorId==actorId&&x.Operation=="PayosCreate"&&x.IdempotencyKey==idempotency,ct);
            if(receipt is not null)
            {
                if(receipt.InputHash!=hash)throw Conflict("IDEMPOTENCY_KEY_CONFLICT","The key was used for different input.");
                op=await db.Set<BillingCheckoutOperation>().AsNoTracking().SingleAsync(x=>x.Id==receipt.ResourceId,ct);
                await tx.CommitAsync(ct);return new(View(op),op.PaymentRequestId.HasValue?200:202);
            }
            await Lock("fet3d:payos:quotation:"+request.QuotationId,ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM quotations WHERE id={request.QuotationId} FOR UPDATE",ct);
            var quote=await db.Quotations.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.QuotationId,ct)??throw Missing();Scope(actor,quote.OrganizationId);
            if(quote.BillingPurpose!="BuildingService"||quote.Status!=QuotationStatus.Accepted||quote.ValidUntil<=Now||quote.Currency!="VND"||quote.TotalAmount<=0||quote.TotalAmount!=decimal.Truncate(quote.TotalAmount))
                throw Conflict("QUOTATION_NOT_PAYABLE","An unexpired Accepted BuildingService quotation in VND is required.");
            if(await db.PayosPaymentRequests.AnyAsync(x=>x.QuotationId==quote.Id&&x.Status==PaymentRequestStatus.Paid,ct))
                throw Conflict("QUOTATION_ALREADY_PAID","The quotation is already paid.");
            var lines=await db.Set<QuotationBuildingItem>().AsNoTracking().Where(x=>x.QuotationId==quote.Id).ToListAsync(ct);
            if(lines.Count==0||await db.Buildings.CountAsync(x=>lines.Select(l=>l.BuildingId).Contains(x.Id)&&x.OrganizationId==quote.OrganizationId&&x.IsActive&&x.DeletedAt==null,ct)!=lines.Count)
                throw Conflict("BUILDING_UNAVAILABLE","Every quotation Building must remain active in the organization.");
            op=(await db.Set<BillingCheckoutOperation>().AsNoTracking().SingleOrDefaultAsync(x=>x.QuotationId==quote.Id&&(x.Status=="Creating"||x.Status=="Ready"||x.Status=="NeedsReconcile"),ct))!;
            var isNew=op is null;
            if(isNew)
            {
                var order=await db.Database.SqlQueryRaw<long>("SELECT nextval('fet3d_payos_order_code') AS \"Value\"").SingleAsync(ct);
                var expires=Now.AddMinutes(30);if(quote.ValidUntil<expires)expires=quote.ValidUntil;
                var input=new PayosCreateInput(order,checked((long)quote.TotalAmount),"FET3D",options.Value.ReturnUrl,options.Value.CancelUrl,expires);
                op=new BillingCheckoutOperation{Id=Guid.NewGuid(),ActorId=actorId,SessionFamilyId=family,QuotationId=quote.Id,IdempotencyKey=idempotency,
                    InputHash=hash,OrderCode=order,ProviderInput=JsonSerializer.Serialize(input),ExpiresAt=expires,Status="Creating",
                    LeaseToken=Guid.NewGuid(),LeaseUntil=Now.AddSeconds(60),Attempts=1,NextAttemptAt=Now,CreatedAt=Now,UpdatedAt=Now};
                db.Add(op);Audit(actorId,quote.OrganizationId,"CheckoutReserved",op.Id);
            }
            db.Add(new BillingCommandReceipt{Id=Guid.NewGuid(),ActorId=actorId,Operation="PayosCreate",IdempotencyKey=idempotency,InputHash=hash,ResourceId=op!.Id,CreatedAt=Now});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            if(!isNew)return new(View(op),op.PaymentRequestId.HasValue?200:202);
        }
        db.ChangeTracker.Clear();await RunCheckout(op!,true,ct);
        op=await db.Set<BillingCheckoutOperation>().AsNoTracking().SingleAsync(x=>x.Id==op!.Id,ct);
        return new(View(op),op.PaymentRequestId.HasValue?201:202);
    }
    public async Task<PayosCheckoutResponse> Checkout(Guid actorId,Guid id,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var actor=await ActorFor(actorId,false,ct);var op=await db.Set<BillingCheckoutOperation>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();
        Scope(actor,await db.Quotations.Where(x=>x.Id==op.QuotationId).Select(x=>x.OrganizationId).SingleAsync(ct));return View(op);
    }
    private async Task RunCheckout(BillingCheckoutOperation op,bool first,CancellationToken ct)
    {
        var lease=op.LeaseToken!.Value;
        try
        {
            var input=Input(op);if(!await Renew(op.Id,lease,ct))return;
            // A previous cancelled checkout can receive a late verified payment. Close any replacement link.
            if(await db.PayosPaymentRequests.AnyAsync(x=>x.QuotationId==op.QuotationId&&x.Status==PaymentRequestStatus.Paid,ct))op.CancelRequested=true;
            PayosLink? link=first?await provider.Create(input,ct):await provider.Get(op.OrderCode,ct);
            if(link is null)
            {
                if(op.CancelRequested||input.ExpiresAt<=Now){await Failure(op,lease,"PAYOS_LINK_NOT_FOUND",false,ct);return;}
                if(!await Renew(op.Id,lease,ct))return;link=await provider.Create(input,ct);
            }
            if(link.OrderCode!=op.OrderCode||link.Amount!=input.Amount||link.Currency!="VND"||string.IsNullOrWhiteSpace(link.PaymentLinkId)||op.PaymentLinkId is not null&&op.PaymentLinkId!=link.PaymentLinkId)
                throw Conflict("PAYOS_PROVIDER_MISMATCH","Provider response differs from the reserved quotation.");
            link=link with {CheckoutUrl=link.CheckoutUrl??"https://pay.payos.vn/web/"+Uri.EscapeDataString(link.PaymentLinkId)};
            // Persist the verified result before binding/cancel. An uncertain copy cannot disappear on crash.
            if(await db.Set<BillingCheckoutOperation>().Where(x=>x.Id==op.Id&&x.LeaseToken==lease&&x.LeaseUntil>Now)
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ProviderResult,JsonSerializer.Serialize(link)).SetProperty(x=>x.PaymentLinkId,link.PaymentLinkId).SetProperty(x=>x.UpdatedAt,Now),ct)!=1)return;
            if(link.Status=="Paid"||link.AmountPaid>0)
            {await Failure(op,lease,"PAYOS_AWAITING_VERIFIED_WEBHOOK",false,ct);return;}
            if(op.CancelRequested&&link.Status is not ("Cancelled" or "Expired"))
            {
                if(!await Renew(op.Id,lease,ct))return;var previous=link;
                link=await provider.Cancel(op.OrderCode,ct);
                if(link.OrderCode!=op.OrderCode||link.PaymentLinkId!=previous.PaymentLinkId||link.Amount!=input.Amount||link.Currency!="VND")throw Conflict("PAYOS_PROVIDER_MISMATCH","Cancellation result mismatch.");
                await db.Set<BillingCheckoutOperation>().Where(x=>x.Id==op.Id&&x.LeaseToken==lease&&x.LeaseUntil>Now)
                    .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ProviderResult,JsonSerializer.Serialize(link)),ct);
            }
            if(!await Renew(op.Id,lease,ct))return;
            if(link.Status is "Cancelled" or "Expired"&&link.AmountPaid==0)
                await executor.Run(false,"SELECT mark_payos_navigation_state(@id,@lease,@status)",ct,("id",op.Id),("lease",lease),("status",link.Status));
            else if(op.PaymentRequestId.HasValue)
                await db.Set<BillingCheckoutOperation>().Where(x=>x.Id==op.Id&&x.LeaseToken==lease&&x.LeaseUntil>Now)
                    .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Ready").SetProperty(x=>x.LastError,(string?)null).SetProperty(x=>x.Attempts,0).SetProperty(x=>x.LeaseToken,(Guid?)null).SetProperty(x=>x.LeaseUntil,(DateTime?)null).SetProperty(x=>x.NextAttemptAt,Now.AddSeconds(30)),ct);
            else await executor.Run(false,"SELECT bind_payos_checkout(@id,@lease)",ct,("id",op.Id),("lease",lease));
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)
        {
            var code=ex is BillingException billing?billing.Code:ex is Npgsql.PostgresException pg?(pg.MessageText.StartsWith("PAYOS_")?pg.MessageText:"PAYOS_DATABASE_"+pg.SqlState):"PAYOS_PROVIDER_UNAVAILABLE";
            logger.LogWarning("PayOS checkout {CheckoutId} attempt {Attempt} failed with {Code}",op.Id,op.Attempts,code);
            await Failure(op,lease,code,code is "PAYOS_SESSION_REVOKED" or "PAYOS_PROVIDER_MISMATCH" or "PAYOS_SCOPE_UNAVAILABLE" or "PAYOS_QUOTATION_EXPIRED" or "PAYOS_QUOTATION_ALREADY_PAID" or "PAYOS_BUILDING_UNAVAILABLE",ct);
        }
    }
    private async Task<bool> Renew(Guid id,Guid lease,CancellationToken ct)=>await db.Set<BillingCheckoutOperation>().Where(x=>x.Id==id&&x.LeaseToken==lease&&x.LeaseUntil>Now)
        .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseUntil,Now.AddSeconds(60)),ct)==1;
    private Task<int> Failure(BillingCheckoutOperation op,Guid lease,string code,bool cancel,CancellationToken ct)=>db.Set<BillingCheckoutOperation>()
        .Where(x=>x.Id==op.Id&&x.LeaseToken==lease&&x.LeaseUntil>Now).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"NeedsReconcile")
            .SetProperty(x=>x.LastError,code).SetProperty(x=>x.CancelRequested,x=>x.CancelRequested||cancel)
            .SetProperty(x=>x.LeaseToken,(Guid?)null).SetProperty(x=>x.LeaseUntil,(DateTime?)null)
            .SetProperty(x=>x.NextAttemptAt,Now.AddSeconds(Math.Min(1800,30*Math.Pow(2,Math.Min(op.Attempts-1,6))))),ct);
    public async Task Recover(CancellationToken ct)
    {
        var ids=await db.Set<BillingCheckoutOperation>().AsNoTracking().Where(x=>(x.Status=="Creating"||x.Status=="NeedsReconcile"||x.Status=="Ready")&&x.Attempts<10&&x.NextAttemptAt<=Now&&(x.LeaseUntil==null||x.LeaseUntil<=Now))
            .OrderBy(x=>x.NextAttemptAt).Select(x=>x.Id).Take(20).ToListAsync(ct);
        foreach(var id in ids)
        {
            var nonce=Guid.NewGuid();var now=Now;
            if(await db.Set<BillingCheckoutOperation>().Where(x=>x.Id==id&&(x.Status=="Creating"||x.Status=="NeedsReconcile"||x.Status=="Ready")&&x.NextAttemptAt<=now&&x.Attempts<10&&(x.LeaseUntil==null||x.LeaseUntil<=now))
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseToken,nonce).SetProperty(x=>x.LeaseUntil,now.AddSeconds(60)).SetProperty(x=>x.Attempts,x=>x.Attempts+1),ct)!=1)continue;
            var op=await db.Set<BillingCheckoutOperation>().AsNoTracking().SingleAsync(x=>x.Id==id,ct);
            await RunCheckout(op,false,ct);
        }
        await RecoverInbox(ct);
        await RecoverProvisioning(ct);
    }
}
