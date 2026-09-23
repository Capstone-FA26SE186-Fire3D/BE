using Fire3D.Application.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
namespace Fire3D.Infrastructure.Authentication;

public sealed class PasswordResetStore(Fire3DDbContext db, IAuthStore accounts) : IPasswordResetStore
{
    public async Task<Guid?> BeginAsync(Guid userId, string firebaseUid, string codeHash, CancellationToken ct)
    {
        await using var tx = await accounts.BeginUserTransactionAsync(userId, ct);
        var user = await accounts.FindUserAsync(userId, ct);
        if (user is null || !user.IsActive || user.DeletedAt.HasValue || user.FirebaseUid != firebaseUid) return null;
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM public.password_reset_operations
              WHERE (user_id={userId} AND status='Pending') OR code_hash={codeHash}) AS "Value"
            """).SingleAsync(ct);
        if (exists) return null;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.password_reset_operations(id,user_id,firebase_uid,code_hash,status)
            VALUES ({id},{userId},{firebaseUid},{codeHash},'Pending')
            """, ct);
        await accounts.RevokeAllUserSessionsAsync(userId, DateTime.UtcNow, ct);
        await tx.CommitAsync(ct);
        return id;
    }
    public async Task FinishAsync(Guid operationId, Guid userId, string outcome, CancellationToken ct)
    {
        if (outcome is not ("Completed" or "Rejected")) throw new ArgumentOutOfRangeException(nameof(outcome));
        await using var tx = await accounts.BeginUserTransactionAsync(userId, ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.password_reset_operations SET status={outcome},finished_at=clock_timestamp()
             WHERE id={operationId} AND user_id={userId} AND status='Pending'
            """, ct);
        if (changed != 1) throw new InvalidOperationException("Reset operation was already finalized or is unavailable.");
        await accounts.RevokeAllUserSessionsAsync(userId, DateTime.UtcNow, ct);
        var user = await accounts.FindUserAsync(userId, ct) ?? throw new InvalidOperationException("Reset account unavailable.");
        await accounts.WriteAuditAsync(user,"Update",userId,DateTime.UtcNow,ct,operationId);
        await tx.CommitAsync(ct);
    }
}

public sealed class PasswordResetQueue(Fire3DDbContext db) : IPasswordResetQueue
{
    public async Task EnqueueAsync(string email, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:reset-email:" + email},0))",ct);
        // Same response for known/unknown accounts, with per-email cooldown before looking up users.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.password_reset_email_jobs(id,email)
            SELECT {Guid.NewGuid()},{email}
             WHERE NOT EXISTS(SELECT 1 FROM public.password_reset_email_jobs
                WHERE email={email} AND created_at > now()-interval '1 minute')
            """,ct);
        await tx.CommitAsync(ct);
    }
    public async Task<ResetEmailJob?> ClaimAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        // An exhausted job may have lost its worker before reporting the final failure.
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE public.password_reset_email_jobs SET status='Dead',lease_token=NULL,lease_until=NULL
             WHERE status='Leased' AND lease_until<=now() AND attempts>=5
            """,ct);
        await using var cmd = new NpgsqlCommand("""
            WITH candidate AS (
              SELECT id FROM public.password_reset_email_jobs
               WHERE attempts<5 AND ((status='Pending' AND available_at<=now())
                    OR (status='Leased' AND lease_until<=now()))
               ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE public.password_reset_email_jobs j
               SET status='Leased',lease_token=@lease,lease_until=now()+interval '2 minutes',attempts=attempts+1
              FROM candidate c WHERE j.id=c.id
            RETURNING j.id,j.email,j.lease_token,j.attempts
            """,(NpgsqlConnection)db.Database.GetDbConnection());
        cmd.Parameters.AddWithValue("lease",Guid.NewGuid());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetGuid(0),r.GetString(1),r.GetGuid(2),r.GetInt32(3)) : null;
    }
    public async Task CompleteAsync(ResetEmailJob job, CancellationToken ct) =>
        _ = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.password_reset_email_jobs SET status='Sent',lease_token=NULL,lease_until=NULL
             WHERE id={job.Id} AND status='Leased' AND lease_token={job.LeaseToken} AND lease_until>now()
            """,ct);
    public async Task FailAsync(ResetEmailJob job, bool permanent, CancellationToken ct)
    {
        var status = permanent || job.Attempt>=5 ? "Dead" : "Pending";
        var delay = TimeSpan.FromSeconds(Math.Min(900,Math.Pow(2,job.Attempt)*15));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.password_reset_email_jobs
               SET status={status},available_at=now()+{delay},lease_token=NULL,lease_until=NULL
             WHERE id={job.Id} AND status='Leased' AND lease_token={job.LeaseToken} AND lease_until>now()
            """,ct);
    }
}
