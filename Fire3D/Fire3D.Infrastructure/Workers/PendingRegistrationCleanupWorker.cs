using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fire3D.Infrastructure.Workers;

/// <summary>Removes expired, never-verified self registrations without cascading into business data.</summary>
public sealed class PendingRegistrationCleanupWorker(IServiceScopeFactory scopes,
    ILogger<PendingRegistrationCleanupWorker> logger) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeleteBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Expired registration cleanup failed.");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> DeleteBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The deletion is deliberately limited to accounts with no business data. If a future
        // feature referenced the account, it remains for support review rather than cascading.
        var removedUsers = new List<(Guid UserId, Guid? OrganizationId)>();
        await using (var command = new NpgsqlCommand($"""
            WITH candidates AS (
              SELECT u.id, u.organization_id
                FROM public.users u
               WHERE u.registration_expires_at <= now() AND u.email_verified_at IS NULL
                 AND u.deleted_at IS NULL
                 AND NOT EXISTS (SELECT 1 FROM public.buildings b WHERE b.organization_id=u.organization_id)
                 AND NOT EXISTS (SELECT 1 FROM public.sessions s WHERE s.trainee_user_id=u.id)
                  -- Verification, resend and delivery all take this lock before a user/token row.
                  -- Cleanup never waits behind one of them: it leaves the account for the next batch.
                  AND pg_try_advisory_xact_lock(hashtextextended('fire3d:verification-account:' || u.id::text, 0))
               ORDER BY u.registration_expires_at
               FOR UPDATE SKIP LOCKED LIMIT {BatchSize}
            ), removed_tokens AS (
              DELETE FROM public.email_verification_tokens t USING candidates c WHERE t.user_id=c.id
            ), removed_jobs AS (
              DELETE FROM public.email_verification_jobs j USING candidates c WHERE j.user_id=c.id
            ), removed_refresh AS (
              DELETE FROM public.auth_refresh_tokens r USING candidates c WHERE r.user_id=c.id
            ), removed_devices AS (
              DELETE FROM public.user_devices d USING candidates c WHERE d.user_id=c.id
            )
            DELETE FROM public.users u USING candidates c
             WHERE u.id=c.id
            RETURNING u.id, u.organization_id
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction()))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                removedUsers.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1)));
        }

        // This is deliberately a second statement. Data-modifying CTEs use one snapshot, so a
        // sibling query cannot observe the deleted user when deciding whether its organization is empty.
        foreach (var removed in removedUsers.Where(user => user.OrganizationId.HasValue))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM public.organizations o
                 WHERE o.id={removed.OrganizationId!.Value}
                   AND o.registration_owner_user_id={removed.UserId}
                   AND NOT EXISTS (SELECT 1 FROM public.users member WHERE member.organization_id=o.id)
                   AND NOT EXISTS (SELECT 1 FROM public.buildings b WHERE b.organization_id=o.id)
                """, ct);
        }
        await transaction.CommitAsync(ct);
        if (removedUsers.Count > 0) logger.LogInformation("Deleted {Count} expired pending registrations.", removedUsers.Count);
        return removedUsers.Count;
    }
}
