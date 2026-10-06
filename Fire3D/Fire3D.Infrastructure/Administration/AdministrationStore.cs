using System.Text.Json;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Fire3D.Infrastructure.Administration;

public sealed class AdministrationStore(Fire3DDbContext db) : IAdministrationStore
{
    public async Task<IAuthTransaction> BeginManagementTransactionAsync(CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Rare management writes serialize with each other and wait for in-flight auth transactions.
            // Ordinary logins/refreshes take a shared lock and remain concurrent across users.
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtextextended('fire3d:identity-management', 0))", ct);
            return new ManagementTransaction(transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken ct) =>
        db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<OrganizationProfileUpdateResult> UpdateOrganizationProfileAsync(Guid organizationId, long revision,
        string name, string? address, string? phoneNumber, DateTime now, CancellationToken ct)
    {
        int changed;
        try
        {
            changed = await db.Organizations.Where(x => x.Id == organizationId && x.IsActive && x.DeletedAt == null && x.ProfileRevision == revision)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, name).SetProperty(x => x.Address, address)
                    .SetProperty(x => x.PhoneNumber, phoneNumber).SetProperty(x => x.ProfileRevision, x => x.ProfileRevision + 1)
                    .SetProperty(x => x.UpdatedAt, now), ct);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation
            && error.ConstraintName == "organizations_phone_normalized_key")
        {
            // The caller returns immediately and disposes its transaction; never query an aborted transaction.
            return OrganizationProfileUpdateResult.PhoneTaken;
        }
        return changed == 1 ? OrganizationProfileUpdateResult.Updated
            : await db.Organizations.AnyAsync(x => x.Id == organizationId && x.IsActive && x.DeletedAt == null, ct)
                ? OrganizationProfileUpdateResult.PreconditionFailed : OrganizationProfileUpdateResult.Unavailable;
    }

    public async Task<bool> TryCreateOrganizationAsync(Organization organization, CancellationToken ct)
    {
        db.Organizations.Add(organization);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "organizations_slug_key" })
        {
            db.Entry(organization).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<PageResponse<OrganizationResponse>> ListOrganizationsAsync(OrganizationFilter filter, CancellationToken ct)
    {
        var query = db.Organizations.AsNoTracking().Where(x => x.DeletedAt == null);
        if (filter.IsActive.HasValue) query = query.Where(x => x.IsActive == filter.IsActive.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Name.ToLower().Contains(search) || x.Slug.Contains(search));
        }
        var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(x => new OrganizationResponse(x.Id, x.Name, x.Slug, x.IsActive, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct);
        return new(items, count, filter.Page, filter.PageSize);
    }

    public async Task<PageResponse<ManagedAccountResponse>> ListAccountsAsync(AccountFilter filter, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking().Where(x => x.DeletedAt == null);
        if (filter.IsActive.HasValue) query = query.Where(x => x.IsActive == filter.IsActive.Value);
        if (filter.Role.HasValue) query = query.Where(x => x.Role == filter.Role.Value);
        if (filter.OrganizationId.HasValue) query = query.Where(x => x.OrganizationId == filter.OrganizationId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Email.Contains(search) || (x.FullName != null && x.FullName.ToLower().Contains(search)));
        }
        var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(x => new ManagedAccountResponse(x.Id, x.Email, x.FullName, x.Role, x.OrganizationId,
                x.IsActive, x.LastLoginAt, x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);
        return new(items, count, filter.Page, filter.PageSize);
    }

    public async Task SetAccountActiveAsync(Guid id, bool active, DateTime now, CancellationToken ct)
    {
        await db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(update =>
            update.SetProperty(x => x.IsActive, active).SetProperty(x => x.UpdatedAt, now), ct);
        if (!active)
            await db.Set<RefreshToken>().Where(x => x.UserId == id && x.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
    }

    public async Task SetOrganizationActiveAsync(Guid id, bool active, DateTime now, CancellationToken ct)
    {
        await db.Organizations.Where(x => x.Id == id && x.IsActive != active).ExecuteUpdateAsync(update =>
            update.SetProperty(x => x.IsActive, active).SetProperty(x => x.ProfileRevision, x => x.ProfileRevision + 1).SetProperty(x => x.UpdatedAt, now), ct);
        if (!active)
            await db.Set<RefreshToken>()
                .Where(x => x.RevokedAt == null && db.Users.Any(u => u.Id == x.UserId && u.OrganizationId == id))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);
    }

    public async Task WriteAuditAsync(Guid actorId, Guid? organizationId, string targetEntity, Guid targetId,
        bool? previousActive, bool active, Guid correlationId, DateTime now, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), UserId = actorId, OrganizationId = organizationId, ActorType = "User",
            Action = previousActive.HasValue ? AuditAction.Update : AuditAction.Create,
            TargetEntity = targetEntity, TargetId = targetId, CorrelationId = correlationId,
            OldValues = previousActive.HasValue ? JsonSerializer.Serialize(new { isActive = previousActive.Value }) : null,
            NewValues = JsonSerializer.Serialize(new { isActive = active }), CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task WriteOrganizationProfileAuditAsync(OrganizationProfileAuditChange change, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), UserId = change.ActorId, OrganizationId = change.OrganizationId, ActorType = "User",
            Action = AuditAction.Update, TargetEntity = "organizations", TargetId = change.OrganizationId,
            CorrelationId = change.CorrelationId,
            OldValues = JsonSerializer.Serialize(new { name = change.OldName, address = change.OldAddress, phoneNumber = change.OldPhoneNumber }),
            NewValues = JsonSerializer.Serialize(new { name = change.NewName, address = change.NewAddress, phoneNumber = change.NewPhoneNumber }),
            CreatedAt = change.CreatedAt
        });
        await db.SaveChangesAsync(ct);
    }

    private sealed class ManagementTransaction(IDbContextTransaction transaction) : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
