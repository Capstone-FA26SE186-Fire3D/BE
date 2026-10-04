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
        if (retentionDays is < 1 or > 365 || batchSize is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Use retention 1-365 days and batch size 1-10000.");
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
        // The gate locks/rechecks/deletes in the implicit statement transaction.
        // API identities keep SELECT/EXECUTE, never unrestricted token DELETE.
        return await db.Database.SqlQuery<int>($"""
            SELECT public.cleanup_expired_refresh_family({candidate.UserId},{candidate.FamilyId},{retentionDays}) AS "Value"
            """).SingleAsync(ct);
    }
}
