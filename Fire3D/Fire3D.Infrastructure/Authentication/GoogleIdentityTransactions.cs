using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Fire3D.Infrastructure.Authentication;

internal static class GoogleIdentityTransactions
{
    // Lifecycle -> Google UID -> user; never wait for a provider while holding these locks.
    internal static async Task<IDbContextTransaction> BeginAsync(Fire3DDbContext db, string uid, Guid? userId, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management',0)); SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:google:" + uid},0))", ct);
            if (userId.HasValue)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:auth:" + userId.Value},0))", ct);
            return tx;
        }
        catch { await tx.DisposeAsync(); throw; }
    }
}
