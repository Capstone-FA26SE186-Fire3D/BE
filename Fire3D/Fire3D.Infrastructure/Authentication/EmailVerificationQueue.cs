using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fire3D.Infrastructure.Authentication;

public sealed class EmailVerificationQueue(Fire3DDbContext db, IOptions<AuthEmailOptions> options) : IEmailVerificationQueue
{
    public async Task EnqueueAsync(string email, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.email_verification_jobs(id,email)
            SELECT {Guid.NewGuid()},{email}
             WHERE NOT EXISTS(SELECT 1 FROM public.email_verification_jobs
                WHERE email={email} AND created_at > now()-interval '1 minute')
            """, ct);
    }

    public async Task<VerificationEmailJob?> ClaimAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE public.email_verification_jobs SET status='Dead',lease_token=NULL,lease_until=NULL
             WHERE status='Leased' AND lease_until<=now() AND attempts>=5
            """, ct);
        await using var command = new NpgsqlCommand("""
            WITH candidate AS (
              SELECT id FROM public.email_verification_jobs WHERE attempts<5 AND
                ((status='Pending' AND available_at<=now()) OR (status='Leased' AND lease_until<=now()))
              ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE public.email_verification_jobs j SET status='Leased',lease_token=@lease,
              lease_until=now()+interval '2 minutes',attempts=attempts+1 FROM candidate c WHERE j.id=c.id
            RETURNING j.id,j.email,j.lease_token,j.attempts
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("lease", Guid.NewGuid());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt32(3)) : null;
    }

    public async Task<string?> CreateLinkAsync(VerificationEmailJob job, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == job.Email && x.IsActive && x.DeletedAt == null && x.EmailVerifiedAt == null, ct);
        if (user is null) return null;
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.email_verification_tokens SET used_at=now() WHERE user_id={user.Id} AND used_at IS NULL", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.email_verification_tokens(id,user_id,token_hash,expires_at) VALUES ({Guid.NewGuid()},{user.Id},{hash},now()+interval '24 hours')", ct);
        await transaction.CommitAsync(ct);
        return options.Value.FrontendUrl.TrimEnd('/') + "/verify-email?token=" + raw;
    }

    public async Task<bool> VerifyAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64 || !token.All(Uri.IsHexDigit)) return false;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var userId = await db.Database.SqlQuery<Guid?>($"""
            UPDATE public.email_verification_tokens SET used_at=now()
             WHERE token_hash={hash} AND used_at IS NULL AND expires_at>now()
             RETURNING user_id AS "Value"
            """).SingleOrDefaultAsync(ct);
        if (userId is null) return false;
        await db.Users.Where(user => user.Id == userId.Value).ExecuteUpdateAsync(setters => setters
            .SetProperty(user => user.EmailVerifiedAt, DateTime.UtcNow)
            .SetProperty(user => user.UpdatedAt, DateTime.UtcNow), ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public Task CompleteAsync(VerificationEmailJob job, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
        UPDATE public.email_verification_jobs SET status='Sent',lease_token=NULL,lease_until=NULL
         WHERE id={job.Id} AND status='Leased' AND lease_token={job.LeaseToken} AND lease_until>now()
        """, ct);
    public Task FailAsync(VerificationEmailJob job, bool permanent, CancellationToken ct)
    {
        var status = permanent || job.Attempt >= 5 ? "Dead" : "Pending";
        var delay = TimeSpan.FromSeconds(Math.Min(900, Math.Pow(2, job.Attempt) * 15));
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.email_verification_jobs SET status={status},available_at=now()+{delay},lease_token=NULL,lease_until=NULL
             WHERE id={job.Id} AND status='Leased' AND lease_token={job.LeaseToken} AND lease_until>now()
            """, ct);
    }
}
