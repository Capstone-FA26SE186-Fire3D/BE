using Fire3D.Application.Authentication;
using Fire3D.Application.Users.Commands.RegisterDevice;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace Fire3D.Infrastructure.Authentication;

public sealed class AuthStore(Fire3DDbContext db) : IAuthStore
{
    public async Task<IAuthTransaction> BeginUserTransactionAsync(Guid userId, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Shared for normal auth; admin status changes take the exclusive lock before revoking sessions.
            // Both locks run in one round trip, preserving lifecycle-before-user order.
            var key = "fire3d:auth:" + userId;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock_shared(hashtextextended('fire3d:identity-management', 0)); SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
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
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email.Trim().ToLower() == email, ct);
    public Task<User?> FindUserByFirebaseUidAsync(string uid, CancellationToken ct) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.FirebaseUid == uid, ct);
    public Task<bool> HasAdminAsync(CancellationToken ct) =>
        db.Users.AnyAsync(x => x.Role == UserRole.PlatformAdmin, ct);
    public Task<int> CountActiveAdminsAsync(CancellationToken ct) =>
        db.Users.CountAsync(x => x.Role == UserRole.PlatformAdmin && x.IsActive && x.DeletedAt == null, ct);
    public Task<bool> OrganizationIsActiveAsync(Guid id, CancellationToken ct) =>
        db.Organizations.AnyAsync(x => x.Id == id && x.IsActive && !x.DeletedAt.HasValue, ct);

    public async Task<bool> TryCreateUserAsync(User user, CancellationToken ct)
    {
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" }) { return false; }
    }

    public async Task<RegisterConflict> TryCreateTraineeAsync(User user, CancellationToken ct)
    {
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return RegisterConflict.None;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName is "users_username_lower_key" or "users_email_key" or "users_email_normalized_key")
        {
            db.Entry(user).State = EntityState.Detached;
            return pg.ConstraintName == "users_username_lower_key"
                ? RegisterConflict.UsernameTaken
                : RegisterConflict.EmailTaken;
        }
    }

    public async Task UpdateUserAsync(User user, CancellationToken ct)
    {
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdatePasswordHashAsync(Guid userId, string passwordHash, DateTime now, CancellationToken ct) =>
        await db.Users.Where(x => x.Id == userId).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.PasswordHash, passwordHash)
            .SetProperty(x => x.UpdatedAt, now), ct);

    public async Task<ProfileUpdateResult> UpdateProfileAsync(Guid userId, long expectedProfileRevision, string? fullName, string? username,
        DateOnly? dob, UserGender? gender, string? phoneNumber, DateTime now, CancellationToken ct)
    {
        try
        {
            var changed = await db.Users
                .Where(x => x.Id == userId && x.IsActive && x.DeletedAt == null && x.ProfileRevision == expectedProfileRevision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.FullName, fullName)
                    .SetProperty(x => x.Username, username)
                    .SetProperty(x => x.Dob, dob)
                    .SetProperty(x => x.Gender, gender)
                    .SetProperty(x => x.PhoneNumber, phoneNumber)
                    .SetProperty(x => x.ProfileRevision, x => x.ProfileRevision + 1)
                    .SetProperty(x => x.UpdatedAt, now), ct);
            return changed == 1 ? ProfileUpdateResult.Updated : ProfileUpdateResult.PreconditionFailed;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "users_username_lower_key")
        {
            return ProfileUpdateResult.UsernameTaken;
        }
    }

    public async Task UpdateLoginAsync(Guid id, DateTime now, CancellationToken ct) =>
        await db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.LastLoginAt, now).SetProperty(x => x.UpdatedAt, now)
            , ct);

    public async Task FinalizePasswordLoginAsync(User user,string? rehashedPassword,RefreshToken token,DateTime now,CancellationToken ct)
    {
        if(db.Database.CurrentTransaction is null) throw new InvalidOperationException("Login requires the lifecycle/user transaction.");
        if(token.UserId!=user.Id || token.CreatedAt!=now) throw new InvalidOperationException("Login token scope does not match identity.");
        await using var command=new NpgsqlCommand("""
            WITH updated AS (
              UPDATE public.users SET last_login_at=@now,updated_at=@now,password_hash=COALESCE(@password,password_hash)
              WHERE id=@user AND NOT EXISTS(SELECT 1 FROM public.password_reset_operations
                WHERE user_id=@user AND (status='Pending' OR finished_at>=@now)) RETURNING id
            ), issued AS (
              INSERT INTO public.auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at)
              SELECT @token,id,@family,@hash,@now,@expires FROM updated RETURNING id
            ), audited AS (
              INSERT INTO public.audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,created_at)
              SELECT @audit,updated.id,@organization,'User','Login','users',updated.id,@correlation,@now FROM updated CROSS JOIN issued
            )
            -- Audit is insert-only for the API role; return the user row instead of reading audit columns.
            SELECT id FROM updated
            """,(NpgsqlConnection)db.Database.GetDbConnection(),(NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction());
        command.Parameters.AddWithValue("user",user.Id);command.Parameters.AddWithValue("now",now);
        command.Parameters.AddWithValue("password",NpgsqlTypes.NpgsqlDbType.Text,(object?)rehashedPassword??DBNull.Value);
        command.Parameters.AddWithValue("organization",NpgsqlTypes.NpgsqlDbType.Uuid,(object?)user.OrganizationId??DBNull.Value);
        command.Parameters.AddWithValue("token",token.Id);command.Parameters.AddWithValue("family",token.FamilyId);
        command.Parameters.AddWithValue("hash",token.TokenHash);command.Parameters.AddWithValue("expires",token.ExpiresAt);
        command.Parameters.AddWithValue("audit",Guid.NewGuid());command.Parameters.AddWithValue("correlation",Guid.NewGuid());
        if(await command.ExecuteScalarAsync(ct) is null)
            throw new PasswordResetException("RESET_PENDING","Sign in again after password recovery completes.",503);
    }

    public async Task<DeviceRegistrationResult> RegisterDeviceAsync(Guid userId, string deviceUuid, DeviceInstallationProof installationProof,
        string? fcmToken, string? deviceModel, string? osVersion, string? appVersion, DateTime now, CancellationToken ct)
    {
        // This lock is independent from the user lock. It serializes an installation moving between accounts.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:installation:" + deviceUuid}, 0))", ct);
        if (fcmToken is not null)
        {
            var tokenDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fcmToken))).ToLowerInvariant();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:fcm-token:" + tokenDigest}, 0))", ct);
        }
        var installation = await db.DeviceInstallations.SingleOrDefaultAsync(x => x.DeviceUuid == deviceUuid, ct);
        if (installation is null)
        {
            installation = new DeviceInstallation { Id = Guid.NewGuid(), DeviceUuid = deviceUuid, SecretHash = installationProof.CurrentHash, SecretHashScheme = DeviceInstallationProof.CurrentHashScheme, CreatedAt = now, UpdatedAt = now };
            db.DeviceInstallations.Add(installation);
        }
        else if (!installationProof.Matches(installation.SecretHash, installation.SecretHashScheme))
        {
            return DeviceRegistrationResult.InstallationKeyMismatch;
        }
        else if (installation.SecretHashScheme != DeviceInstallationProof.CurrentHashScheme || installation.SecretHash != installationProof.CurrentHash)
        {
            installation.SecretHash = installationProof.CurrentHash;
            installation.SecretHashScheme = DeviceInstallationProof.CurrentHashScheme;
            installation.UpdatedAt = now;
        }

        if (fcmToken is not null && await db.UserDevices.AnyAsync(x => x.FcmToken == fcmToken && x.NotificationsEnabled && x.RevokedAt == null && x.DeviceUuid != deviceUuid, ct))
            return DeviceRegistrationResult.TokenAlreadyBound;

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
                AppVersion = appVersion,
                InstallationId = installation.Id,
                NotificationsEnabled = fcmToken is not null,
                FcmTokenGeneration = 1,
                LastSeenAt = now,
                CreatedAt = now
            };
            db.UserDevices.Add(device);
        }
        else
        {
            device.InstallationId = installation.Id;
            device.FcmToken = fcmToken;
            device.NotificationsEnabled = fcmToken is not null;
            device.RevokedAt = null;
            if (fcmToken is not null) device.FcmTokenGeneration++;
            device.DeviceModel = deviceModel ?? device.DeviceModel;
            device.OsVersion = osVersion ?? device.OsVersion;
            device.AppVersion = appVersion ?? device.AppVersion;
            device.LastSeenAt = now;
            db.UserDevices.Update(device);
        }
        // A device may move accounts, but its previous push binding must be disabled first.
        await db.UserDevices.Where(x => x.InstallationId == installation.Id && x.UserId != userId && x.NotificationsEnabled)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NotificationsEnabled, false)
                .SetProperty(x => x.FcmToken, (string?)null).SetProperty(x => x.RevokedAt, now).SetProperty(x => x.LastSeenAt, now), ct);
        try
        {
            await db.SaveChangesAsync(ct);
            return DeviceRegistrationResult.Registered;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && ex.ConstraintName == "ux_user_devices_active_fcm_token")
        {
            return DeviceRegistrationResult.TokenAlreadyBound;
        }
    }

    public async Task<DeviceRevokeResult> RevokeDeviceAsync(Guid userId, string deviceUuid, DeviceInstallationProof installationProof, DateTime now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:installation:" + deviceUuid}, 0))", ct);
        var installation = await db.DeviceInstallations.SingleOrDefaultAsync(x => x.DeviceUuid == deviceUuid, ct);
        if (installation is null)
            return DeviceRevokeResult.Revoked;
        if (!installationProof.Matches(installation.SecretHash, installation.SecretHashScheme))
            return DeviceRevokeResult.InstallationKeyMismatch;
        if (installation.SecretHashScheme != DeviceInstallationProof.CurrentHashScheme || installation.SecretHash != installationProof.CurrentHash)
        {
            installation.SecretHash = installationProof.CurrentHash;
            installation.SecretHashScheme = DeviceInstallationProof.CurrentHashScheme;
            installation.UpdatedAt = now;
        }
        await db.UserDevices.Where(x => x.UserId == userId && x.DeviceUuid == deviceUuid && x.InstallationId == installation.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.FcmToken, (string?)null)
                .SetProperty(x => x.NotificationsEnabled, false)
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.LastSeenAt, now), ct);
        return DeviceRevokeResult.Revoked;
    }

    public Task DisableUserPushDevicesAsync(Guid userId, DateTime now, CancellationToken ct) =>
        db.UserDevices.Where(x => x.UserId == userId && x.NotificationsEnabled)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.FcmToken, (string?)null)
                .SetProperty(x => x.NotificationsEnabled, false).SetProperty(x => x.RevokedAt, now).SetProperty(x => x.LastSeenAt, now), ct);

    public Task<RefreshToken?> FindRefreshTokenAsync(string hash, CancellationToken ct) =>
        db.Set<RefreshToken>().AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
    public async Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct)
    {
        // All session issuance is serialized against reset, even legacy callers without a transaction.
        await using var owned = db.Database.CurrentTransaction is null
            ? await BeginUserTransactionAsync(token.UserId, ct) : null;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"fire3d:auth:" + token.UserId},0))",ct);
        var fenced = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM public.password_reset_operations WHERE user_id={token.UserId}
              AND (status='Pending' OR finished_at >= {token.CreatedAt})) AS "Value"
            """).SingleAsync(ct);
        if (fenced) throw new PasswordResetException("RESET_PENDING", "Sign in again after password recovery completes.", 503);
        db.Set<RefreshToken>().Add(token);
        await db.SaveChangesAsync(ct);
        if (owned is not null) await owned.CommitAsync(ct);
    }
    public async Task ConsumeRefreshTokenAsync(Guid id, DateTime now, CancellationToken ct) =>
        await db.Set<RefreshToken>().Where(x => x.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConsumedAt, now), ct);
        public async Task RevokeAllUserSessionsAsync(Guid userId, DateTime revokedAt, CancellationToken ct) =>
        await db.Set<RefreshToken>().Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, revokedAt), ct);

    public async Task RevokeFamilyAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct) =>
        await db.Set<RefreshToken>().Where(x => x.UserId == userId && x.FamilyId == familyId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
    public Task<bool> FamilyIsActiveAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct) =>
        db.Set<RefreshToken>().AnyAsync(x => x.UserId == userId && x.FamilyId == familyId
            && x.ConsumedAt == null && x.RevokedAt == null && x.ExpiresAt > now, ct);
    public Task<bool> SessionIsValidAsync(Guid userId,Guid familyId,string? role,string? organizationId,DateTime now,CancellationToken ct)
    {
        if(!Enum.TryParse<UserRole>(role,false,out var expectedRole) || !Enum.IsDefined(expectedRole)
            || expectedRole.ToString()!=role) return Task.FromResult(false);
        Guid? expectedOrganization=null;
        if(organizationId is not null)
        {
            if(!Guid.TryParseExact(organizationId,"D",out var parsed) || parsed.ToString()!=organizationId)
                return Task.FromResult(false);
            expectedOrganization=parsed;
        }
        if((expectedRole==UserRole.OrganizationUser)!=expectedOrganization.HasValue) return Task.FromResult(false);
        return db.Users.AnyAsync(user=>user.Id==userId && user.IsActive && user.DeletedAt==null
            && user.Role==expectedRole && user.OrganizationId==expectedOrganization
            && (user.RegistrationExpiresAt==null || user.EmailVerifiedAt!=null)
            && (user.OrganizationId==null || db.Organizations.Any(org=>org.Id==user.OrganizationId && org.IsActive && org.DeletedAt==null))
            && db.Set<RefreshToken>().Any(token=>token.UserId==userId && token.FamilyId==familyId
                && token.ConsumedAt==null && token.RevokedAt==null && token.ExpiresAt>now),ct);
    }
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

    // Password reset
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

    public async Task InvalidateUserResetTokensAsync(Guid userId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Reset-token invalidation requires the user transaction.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT public.invalidate_legacy_reset_tokens({userId})", ct);
    }

    // Registration
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
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName is "organizations_slug_key" or "users_username_lower_key" or "users_email_key" or "users_email_normalized_key" or "organizations_phone_normalized_key")
        {
            db.Entry(organization).State = EntityState.Detached;
            db.Entry(user).State = EntityState.Detached;
            return pg.ConstraintName == "organizations_phone_normalized_key"
                ? RegisterConflict.OrganizationPhoneTaken
                : pg.ConstraintName == "organizations_slug_key"
                    ? RegisterConflict.SlugTaken
                    : pg.ConstraintName == "users_username_lower_key"
                        ? RegisterConflict.UsernameTaken
                        : RegisterConflict.EmailTaken;
        }
    }
    public Task EnqueuePasswordResetAsync(string email, CancellationToken ct) =>
        new PasswordResetQueue(db).EnqueueAsync(email, ct);
}
