using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fire3D.Infrastructure.Authentication;

/// <summary>
/// PostgreSQL is the source of truth for verification work. Redis only limits requests and
/// accelerates dispatch; a missing Redis connection must never cause this store to fabricate work.
/// </summary>
public sealed class EmailVerificationQueue(Fire3DDbContext db, IOptions<AuthEmailOptions> options) : IEmailVerificationQueue
{
    public async Task EnqueueAsync(Guid userId, string email, CancellationToken ct)
    {
        await InsertJobAsync(userId, email, startNewGeneration: false, ct);
    }

    public async Task EnqueueAsync(string email, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, ct);
        if (user is null || !user.RegistrationExpiresAt.HasValue || user.EmailVerifiedAt.HasValue ||
            user.RegistrationExpiresAt <= DateTime.UtcNow)
            return; // indistinguishable 202 prevents account enumeration.
        await InsertJobAsync(user.Id, user.Email, startNewGeneration: true, ct);
    }

    private async Task InsertJobAsync(Guid userId, string email, bool startNewGeneration, CancellationToken ct)
    {
        await using var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:verification-account:" + userId}, 0))", ct);

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.Email == email, ct);
        if (user is null || !user.RegistrationExpiresAt.HasValue || user.EmailVerifiedAt.HasValue ||
            user.RegistrationExpiresAt <= DateTime.UtcNow)
        {
            if (owned is not null) await owned.CommitAsync(ct);
            return;
        }

        var generation = await db.Database.SqlQuery<int>($"""
            SELECT COALESCE(MAX(generation), 0) AS "Value"
            FROM public.email_verification_jobs WHERE user_id={userId}
            """).SingleAsync(ct);
        if (startNewGeneration)
        {
            generation++;
        }
        else if (generation == 0)
        {
            generation = 1;
        }

        // Initial registration is idempotent inside its transaction. Resend creates a new
        // delivery generation, but already-issued links remain usable until their own expiry.
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM public.email_verification_jobs
              WHERE user_id={userId} AND generation={generation}
                AND created_at > now() - interval '1 minute') AS "Value"
            """).SingleAsync(ct);
        if (!exists)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public.email_verification_jobs(id,user_id,email,generation)
                VALUES ({Guid.NewGuid()},{userId},{email},{generation})
                """, ct);
        if (owned is not null) await owned.CommitAsync(ct);
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
            RETURNING j.id,j.user_id,j.email,j.lease_token,j.attempts
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("lease", Guid.NewGuid());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new VerificationEmailJob(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetInt32(4))
            : null;
    }

    public async Task<string?> CreateLinkAsync(VerificationEmailJob job, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:verification-account:" + job.UserId}, 0))", ct);
        var ownsLease = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM public.email_verification_jobs
              WHERE id={job.Id} AND user_id={job.UserId} AND status='Leased'
                AND lease_token={job.LeaseToken} AND lease_until>now()
                AND generation=(SELECT MAX(generation) FROM public.email_verification_jobs WHERE user_id={job.UserId})) AS "Value"
            """).SingleAsync(ct);
        if (!ownsLease) return null;

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.UserId && x.Email == job.Email
            && x.IsActive && x.DeletedAt == null && x.EmailVerifiedAt == null && x.RegistrationExpiresAt > DateTime.UtcNow, ct);
        if (user is null) return null;

        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Hash(raw);
        // Retry may create another valid token, but never invalidates a possibly delivered link.
        // Only an explicit resend invalidates the previous generation under the same account lock.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.email_verification_tokens(id,user_id,token_hash,expires_at)
            VALUES ({Guid.NewGuid()},{user.Id},{hash},LEAST({user.RegistrationExpiresAt!.Value}, now()+interval '15 minutes'))
            """, ct);
        await transaction.CommitAsync(ct);
        return options.Value.GetVerificationPageBaseUrl() + "/verify-email#token=" + raw;
    }

    public Task<bool> CanDeliverAsync(VerificationEmailJob job, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM public.email_verification_jobs
              WHERE id={job.Id} AND user_id={job.UserId} AND status='Leased'
                AND lease_token={job.LeaseToken} AND lease_until>now()
                AND generation=(SELECT MAX(generation) FROM public.email_verification_jobs WHERE user_id={job.UserId})) AS "Value"
            """).SingleAsync(ct);

    public async Task<bool> VerifyAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64 || !token.All(Uri.IsHexDigit)) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hash = Hash(token.ToLowerInvariant());
        // Serialize token consumption, resend and delivery per account before taking the token row.
        // Taking two different token locks before the shared account row can deadlock concurrent
        // verification requests after a retry has minted more than one active token.
        var tokenUserId = await db.Database.SqlQuery<Guid>($"""
            SELECT user_id AS "Value" FROM public.email_verification_tokens
             WHERE token_hash={hash} AND used_at IS NULL AND expires_at>now()
            """).SingleOrDefaultAsync(ct);
        if (tokenUserId == Guid.Empty) return false;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:verification-account:" + tokenUserId}, 0))", ct);
        await using var command = new NpgsqlCommand("""
            UPDATE public.email_verification_tokens t SET used_at=now()
              FROM public.users u
             WHERE t.token_hash=@hash AND t.used_at IS NULL AND t.expires_at>now()
               AND t.user_id=u.id AND u.is_active AND u.deleted_at IS NULL
               AND u.email_verified_at IS NULL AND u.registration_expires_at>now()
             RETURNING t.user_id
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
        command.Parameters.AddWithValue("hash", hash);
        var result = await command.ExecuteScalarAsync(ct);
        if (result is not Guid userId) return false;
        await db.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(setters => setters
            .SetProperty(user => user.EmailVerifiedAt, DateTime.UtcNow)
            .SetProperty(user => user.RegistrationExpiresAt, (DateTime?)null)
            .SetProperty(user => user.UpdatedAt, DateTime.UtcNow), ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.email_verification_tokens SET used_at=now()
             WHERE user_id={userId} AND used_at IS NULL
            """, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public Task CompleteAsync(VerificationEmailJob job, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
        UPDATE public.email_verification_jobs SET status='Sent',lease_token=NULL,lease_until=NULL
         WHERE id={job.Id} AND user_id={job.UserId} AND status='Leased' AND lease_token={job.LeaseToken}
        """, ct);

    public Task FailAsync(VerificationEmailJob job, bool permanent, CancellationToken ct)
    {
        var status = permanent || job.Attempt >= 5 ? "Dead" : "Pending";
        var delay = TimeSpan.FromSeconds(Math.Min(900, Math.Pow(2, job.Attempt) * 15));
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.email_verification_jobs SET status={status},available_at=now()+{delay},lease_token=NULL,lease_until=NULL
             WHERE id={job.Id} AND user_id={job.UserId} AND status='Leased' AND lease_token={job.LeaseToken}
            """, ct);
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}
