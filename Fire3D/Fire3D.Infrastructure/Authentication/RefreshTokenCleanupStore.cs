using System.Data;
using Fire3D.Application.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fire3D.Infrastructure.Authentication;

/// <summary>Physically removes only refresh-token families whose absolute expiry has passed the retention window.</summary>
public sealed class RefreshTokenCleanupStore(Fire3DDbContext db) : IRefreshTokenCleanupStore
{
    private sealed record Candidate(Guid UserId, Guid FamilyId);

    public async Task<int> DeleteExpiredFamiliesAsync(int retentionDays, int batchSize, CancellationToken ct)
    {
        var candidates = await ReadCandidatesAsync(retentionDays, batchSize, ct);
        var deleted = 0;
        foreach (var candidate in candidates)
            deleted += await DeleteFamilyIfStillExpiredAsync(candidate, retentionDays, ct);
        return deleted;
    }

    private async Task<List<Candidate>> ReadCandidatesAsync(int retentionDays, int batchSize, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await connection.OpenAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT user_id, family_id
                  FROM public.auth_refresh_tokens
                 GROUP BY user_id, family_id
                HAVING max(expires_at) <= statement_timestamp() - (@retention_days * interval '1 day')
                 ORDER BY max(expires_at), user_id, family_id
                 LIMIT @batch_size
                """, connection);
            command.Parameters.AddWithValue("retention_days", retentionDays);
            command.Parameters.AddWithValue("batch_size", batchSize);

            var candidates = new List<Candidate>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                candidates.Add(new Candidate(reader.GetGuid(0), reader.GetGuid(1)));
            return candidates;
        }
        finally
        {
            if (closeConnection) await connection.CloseAsync();
        }
    }

    private async Task<int> DeleteFamilyIfStillExpiredAsync(Candidate candidate, int retentionDays, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management', 0))", ct);
        var userLock = "fire3d:auth:" + candidate.UserId;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({userLock}, 0))", ct);

        var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM public.auth_refresh_tokens token
             WHERE token.user_id={candidate.UserId}
               AND token.family_id={candidate.FamilyId}
               AND NOT EXISTS (
                   SELECT 1
                     FROM public.auth_refresh_tokens member
                    WHERE member.user_id={candidate.UserId}
                      AND member.family_id={candidate.FamilyId}
                      AND member.expires_at > statement_timestamp() - ({retentionDays} * interval '1 day')
               )
            """, ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }
}
