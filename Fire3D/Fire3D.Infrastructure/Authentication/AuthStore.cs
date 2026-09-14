using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Fire3D.Infrastructure.Authentication;

public sealed class AuthStore(Fire3DDbContext db) : IAuthStore
{
    public async Task<IAuthTransaction> BeginUserTransactionAsync(Guid userId, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // The same user lock serializes login, refresh, replay revocation and logout across API instances.
            var key = "fire3d:auth:" + userId;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            return new AuthTransaction(transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public Task<User?> FindUserAsync(Guid id, CancellationToken ct) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<User?> FindUserByEmailAsync(string email, CancellationToken ct) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, ct);
    public Task<bool> HasAdminAsync(CancellationToken ct) =>
        db.Users.AnyAsync(x => x.Role == UserRole.PlatformAdmin, ct);
    public Task<bool> OrganizationIsActiveAsync(Guid id, CancellationToken ct) =>
        db.Organizations.AnyAsync(x => x.Id == id && x.IsActive && x.DeletedAt == null, ct);

    public async Task<bool> TryCreateUserAsync(User user, CancellationToken ct)
    {
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "users_email_key" })
        {
            db.Entry(user).State = EntityState.Detached;
            return false;
        }
    }

    public async Task UpdateLoginAsync(Guid id, DateTime now, string passwordHash, CancellationToken ct) =>
        await db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.LastLoginAt, now).SetProperty(x => x.UpdatedAt, now)
            .SetProperty(x => x.PasswordHash, passwordHash), ct);
    public Task<RefreshToken?> FindRefreshTokenAsync(string hash, CancellationToken ct) =>
        db.Set<RefreshToken>().AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
    public async Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct)
    {
        db.Set<RefreshToken>().Add(token);
        await db.SaveChangesAsync(ct);
    }
    public async Task ConsumeRefreshTokenAsync(Guid id, DateTime now, CancellationToken ct) =>
        await db.Set<RefreshToken>().Where(x => x.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConsumedAt, now), ct);
    public async Task RevokeFamilyAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct) =>
        await db.Set<RefreshToken>().Where(x => x.UserId == userId && x.FamilyId == familyId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
    public Task<bool> FamilyIsActiveAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct) =>
        db.Set<RefreshToken>().AnyAsync(x => x.UserId == userId && x.FamilyId == familyId
            && x.ConsumedAt == null && x.RevokedAt == null && x.ExpiresAt > now, ct);
    public async Task WriteAuditAsync(User actor, string action, Guid targetId, DateTime now, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), UserId = actor.Id, OrganizationId = actor.OrganizationId,
            ActorType = "User", Action = Enum.Parse<AuditAction>(action), TargetEntity = "users",
            TargetId = targetId, CorrelationId = Guid.NewGuid(), CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
    }

    private sealed class AuthTransaction(IDbContextTransaction transaction) : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
