using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
namespace Fire3D.Infrastructure.Billing;
public sealed partial class PayosPayments
{
    public async Task Receive(JsonElement body,CancellationToken ct)
    {
        // The SDK verifies the original signed data before normalization. No bank/customer PII is persisted.
        var ev=await provider.Verify(body,ct);var key="payos:"+options.Value.ClientId+":"+ev.Reference;var hash=Hash(ev);
        await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock("fet3d:payos:inbox:"+key,ct);
        var old=await db.Set<PayosWebhookInboxEntry>().AsNoTracking().SingleOrDefaultAsync(x=>x.EventKey==key,ct);
        if(old is not null){if(old.InputHash!=hash)throw Conflict("PAYOS_EVENT_CONFLICT","The provider reference was already used for different verified data.");return;}
        db.Add(new PayosWebhookInboxEntry{Id=Guid.NewGuid(),EventKey=key,InputHash=hash,OrderCode=ev.OrderCode,Payload=JsonSerializer.Serialize(ev),
            VerifiedAt=Now,Status="Pending",NextAttemptAt=Now,CreatedAt=Now});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    private async Task RecoverInbox(CancellationToken ct)
    {
        var ids=await db.Set<PayosWebhookInboxEntry>().AsNoTracking().Where(x=>(x.Status=="Pending"||x.Status=="NeedsReconcile")&&x.Attempts<10&&x.NextAttemptAt<=Now&&(x.LeaseUntil==null||x.LeaseUntil<=Now))
            .OrderBy(x=>x.NextAttemptAt).Select(x=>x.Id).Take(20).ToListAsync(ct);
        foreach(var id in ids)
        {
            var lease=Guid.NewGuid();var now=Now;
            if(await db.Set<PayosWebhookInboxEntry>().Where(x=>x.Id==id&&(x.Status=="Pending"||x.Status=="NeedsReconcile")&&x.NextAttemptAt<=now&&x.Attempts<10&&(x.LeaseUntil==null||x.LeaseUntil<=now))
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseToken,lease).SetProperty(x=>x.LeaseUntil,now.AddSeconds(60)).SetProperty(x=>x.Attempts,x=>x.Attempts+1),ct)!=1)continue;
            try{await executor.Run(true,"SELECT process_payos_inbox(@id,@lease)",ct,("id",id),("lease",lease));}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                var code=ex is Npgsql.PostgresException pg?(pg.MessageText.StartsWith("PAYOS_")?pg.MessageText:"PAYOS_INBOX_DATABASE_"+pg.SqlState):"PAYOS_INBOX_DATABASE_UNAVAILABLE";
                var attempt=await db.Set<PayosWebhookInboxEntry>().Where(x=>x.Id==id).Select(x=>x.Attempts).SingleAsync(ct);
                logger.LogWarning("PayOS inbox {JobId} attempt {Attempt} failed with {Code}",id,attempt,code);
                await db.Set<PayosWebhookInboxEntry>().Where(x=>x.Id==id&&x.LeaseToken==lease&&x.LeaseUntil>Now)
                    .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"NeedsReconcile").SetProperty(x=>x.LastError,code).SetProperty(x=>x.LeaseToken,(Guid?)null).SetProperty(x=>x.LeaseUntil,(DateTime?)null)
                    .SetProperty(x=>x.NextAttemptAt,Now.AddSeconds(Math.Min(1800,30*Math.Pow(2,Math.Min(attempt-1,6))))),ct);
            }
        }
    }
}
