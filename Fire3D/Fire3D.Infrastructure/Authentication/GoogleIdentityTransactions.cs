using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Fire3D.Infrastructure.Authentication;

internal static class GoogleIdentityTransactions
{
    internal static Task<int> LockUserAsync(Fire3DDbContext db, Guid userId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:auth:" + userId},0))", ct);
    // Lifecycle -> Google UID -> user; never wait for a provider while holding these locks.
    internal static async Task<IDbContextTransaction> BeginAsync(Fire3DDbContext db, string uid, Guid? userId, CancellationToken ct, bool boundLockWait = false)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (boundLockWait) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0)); SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:google:" + uid},0))", ct);
            if (userId.HasValue)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:auth:" + userId.Value},0))", ct);
            return tx;
        }
        catch { await tx.DisposeAsync(); throw; }
    }
}
