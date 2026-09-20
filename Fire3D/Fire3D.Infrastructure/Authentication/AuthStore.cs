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
            // Shared for normal auth; admin status changes take the exclusive lock before revoking sessions.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management', 0))", ct);
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
    public Task<User?> FindUserByFirebaseUidAsync(string uid, CancellationToken ct) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.FirebaseUid == uid, ct);
    public Task<bool> HasAdminAsync(CancellationToken ct) =>
        db.Users.AnyAsync(x => x.Role == UserRole.PlatformAdmin, ct);
    public Task<bool> OrganizationIsActiveAsync(Guid id, CancellationToken ct) =>
        db.Organizations.AnyAsync(x => x.Id == id && x.IsActive && !x.DeletedAt.HasValue, ct);

    public async Task<bool> TryCreateUserAsync(User user, CancellationToken ct)
    {
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" }) { return false; }
    }

    public async Task UpdateUserAsync(User user, CancellationToken ct)
    {
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateLoginAsync(Guid id, DateTime now, CancellationToken ct) =>
        await db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.LastLoginAt, now).SetProperty(x => x.UpdatedAt, now)
            , ct);

    public async Task<bool> UpsertDeviceAsync(Guid userId, string deviceUuid, string? fcmToken, string? deviceModel, string? osVersion, CancellationToken ct)
    {
        var device = await db.UserDevices.FirstOrDefaultAsync(x => x.UserId == userId && x.DeviceUuid == deviceUuid, ct);
        if (device == null)
        {
            device = new UserDevice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DeviceUuid = deviceUuid,
                FcmToken = fcmToken,
                DeviceModel = deviceModel,
                OsVersion = osVersion,
                LastSeenAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            db.UserDevices.Add(device);
        }
        else
        {
            device.FcmToken = fcmToken ?? device.FcmToken;
            device.DeviceModel = deviceModel ?? device.DeviceModel;
            device.OsVersion = osVersion ?? device.OsVersion;
            device.LastSeenAt = DateTime.UtcNow;
            db.UserDevices.Update(device);
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

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
    public async Task WriteAuditAsync(User actor, string action, Guid targetId, DateTime now, CancellationToken ct, Guid? correlationId = null)
    {
        var scope = action == "Create"
            ? await db.Users.Where(x => x.Id == targetId).Select(x => x.OrganizationId).SingleAsync(ct)
            : actor.OrganizationId;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), UserId = actor.Id, OrganizationId = scope,
            ActorType = "User", Action = Enum.Parse<AuditAction>(action), TargetEntity = "users",
            TargetId = targetId, CorrelationId = correlationId ?? Guid.NewGuid(), CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
    }

    private sealed class AuthTransaction(IDbContextTransaction transaction) : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    // ── Password Reset ────────────────────────────────────────────────────────
    public async Task SavePasswordResetTokenAsync(PasswordResetToken token, CancellationToken ct)
    {
        db.Set<PasswordResetToken>().Add(token);
        await db.SaveChangesAsync(ct);
    }

    public Task<PasswordResetToken?> FindValidResetTokenAsync(Guid tokenId, CancellationToken ct) =>
        db.Set<PasswordResetToken>()
          .AsNoTracking()
          .SingleOrDefaultAsync(
              x => x.Id == tokenId && x.UsedAt == null && x.ExpiresAt > DateTime.UtcNow, ct);

    public async Task MarkResetTokenUsedAsync(Guid tokenId, DateTime now, CancellationToken ct) =>
        await db.Set<PasswordResetToken>()
                .Where(x => x.Id == tokenId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, now), ct);

    public async Task InvalidateUserResetTokensAsync(Guid userId, CancellationToken ct) =>
        await db.Set<PasswordResetToken>()
                .Where(x => x.UserId == userId && x.UsedAt == null)
                .ExecuteDeleteAsync(ct);

    // ── Registration ──────────────────────────────────────────────────────────
    public async Task<RegisterConflict> TryCreateOrganizationWithUserAsync(Organization organization, User user, CancellationToken ct)
    {
        db.Organizations.Add(organization);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return RegisterConflict.None;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            db.Entry(organization).State = EntityState.Detached;
            db.Entry(user).State = EntityState.Detached;
            return pg.ConstraintName == "organizations_slug_key"
                ? RegisterConflict.SlugTaken
                : RegisterConflict.EmailTaken;
        }
    }
}
