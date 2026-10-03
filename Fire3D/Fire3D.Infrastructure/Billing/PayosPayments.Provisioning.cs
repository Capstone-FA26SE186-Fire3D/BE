using System.Text.Json;
using Fire3D.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
namespace Fire3D.Infrastructure.Billing;
public sealed partial class PayosPayments
{
    private sealed record ProvisioningContext(DateTimeOffset Activation,DateTimeOffset? LastEnd,int Months,Guid? ExistingId);
    internal static (DateTime Start,DateTime End) Period(DateTimeOffset activation,DateTimeOffset? lastEnd,int months)
    {
        var start=(lastEnd>activation?lastEnd.Value:activation).UtcDateTime;return(start,start.AddMonths(months));
    }
    private async Task RecoverProvisioning(CancellationToken ct)
    {
        var ids=await db.Set<PaymentProvisioningRecord>().AsNoTracking().Where(x=>(x.Status=="Pending"||x.Status=="NeedsReconcile")&&x.Attempts<10&&x.NextAttemptAt<=Now&&(x.LeaseUntil==null||x.LeaseUntil<=Now))
            .OrderBy(x=>x.NextAttemptAt).Select(x=>x.Id).Take(50).ToListAsync(ct);
        foreach(var id in ids)
        {
            var lease=Guid.NewGuid();var now=Now;
            if(await db.Set<PaymentProvisioningRecord>().Where(x=>x.Id==id&&(x.Status=="Pending"||x.Status=="NeedsReconcile")&&x.NextAttemptAt<=now&&x.Attempts<10&&(x.LeaseUntil==null||x.LeaseUntil<=now))
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseToken,lease).SetProperty(x=>x.LeaseUntil,now.AddSeconds(60)).SetProperty(x=>x.Attempts,x=>x.Attempts+1),ct)!=1)continue;
            try
            {
                await executor.Transaction(true,async(connection,transaction)=>
                {
                    // No provider call inside this bounded transaction. Locks and authoritative data live on the executor connection.
                    await using var read=new NpgsqlCommand("SELECT claim_payos_provisioning_context(@id,@lease)",connection,transaction){CommandTimeout=20};
                    read.Parameters.AddWithValue("id",id);read.Parameters.AddWithValue("lease",lease);
                    var input=JsonSerializer.Deserialize<ProvisioningContext>((string)(await read.ExecuteScalarAsync(ct))!)!;
                    var (start,end)=Period(input.Activation,input.LastEnd,input.Months);
                    await using var write=new NpgsqlCommand("SELECT finalize_payos_provisioning(@id,@lease,@start,@end)",connection,transaction){CommandTimeout=20};
                    write.Parameters.AddWithValue("id",id);write.Parameters.AddWithValue("lease",lease);write.Parameters.AddWithValue("start",start);write.Parameters.AddWithValue("end",end);
                    return await write.ExecuteScalarAsync(ct);
                },ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                var code=ex is PostgresException pg?(pg.MessageText.StartsWith("PAYOS_")?pg.MessageText:"PAYOS_PROVISIONING_DATABASE_"+pg.SqlState):"PAYOS_PROVISIONING_UNAVAILABLE";
                var attempt=await db.Set<PaymentProvisioningRecord>().Where(x=>x.Id==id).Select(x=>x.Attempts).SingleAsync(ct);
                logger.LogWarning("PayOS provisioning {JobId} attempt {Attempt} failed with {Code}",id,attempt,code);
                await db.Set<PaymentProvisioningRecord>().Where(x=>x.Id==id&&x.LeaseToken==lease&&x.LeaseUntil>Now)
                    .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"NeedsReconcile").SetProperty(x=>x.LastError,code).SetProperty(x=>x.LeaseToken,(Guid?)null).SetProperty(x=>x.LeaseUntil,(DateTime?)null)
                        .SetProperty(x=>x.NextAttemptAt,Now.AddSeconds(Math.Min(1800,30*Math.Pow(2,Math.Min(attempt-1,6))))).SetProperty(x=>x.UpdatedAt,Now),ct);
            }
        }
    }
}
