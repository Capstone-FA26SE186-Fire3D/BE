using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    private async Task DeleteBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Fire3DDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The deletion is deliberately limited to accounts with no business data. If a future
        // feature referenced the account, it remains for support review rather than cascading.
        var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH candidates AS (
              SELECT u.id, u.organization_id
                FROM public.users u
               WHERE u.registration_expires_at <= now() AND u.email_verified_at IS NULL
                 AND u.deleted_at IS NULL
                 AND NOT EXISTS (SELECT 1 FROM public.buildings b WHERE b.organization_id=u.organization_id)
                 AND NOT EXISTS (SELECT 1 FROM public.sessions s WHERE s.trainee_user_id=u.id)
               ORDER BY u.registration_expires_at
               FOR UPDATE SKIP LOCKED LIMIT {BatchSize}
            ), removed_audit AS (
              DELETE FROM public.audit_logs a USING candidates c
               WHERE a.user_id=c.id AND a.target_id=c.id AND a.target_entity='users' AND a.action='Create'
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
            """, ct);

        // Only the organization created by the expired OrganizationUser is eligible. Existing
        // organizations and any organization containing a second account are preserved.
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM public.organizations o
             WHERE NOT EXISTS (SELECT 1 FROM public.users u WHERE u.organization_id=o.id)
               AND NOT EXISTS (SELECT 1 FROM public.buildings b WHERE b.organization_id=o.id)
               AND o.slug LIKE 'org-%'
               AND o.created_at <= now() - interval '2 hours';
            """, ct);
        await transaction.CommitAsync(ct);
        if (deleted > 0) logger.LogInformation("Deleted {Count} expired pending registrations.", deleted);
    }
}
