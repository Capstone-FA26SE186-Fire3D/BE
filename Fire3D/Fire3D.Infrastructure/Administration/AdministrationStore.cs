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
        await db.Organizations.Where(x => x.Id == id).ExecuteUpdateAsync(update =>
            update.SetProperty(x => x.IsActive, active).SetProperty(x => x.UpdatedAt, now), ct);
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

    private sealed class ManagementTransaction(IDbContextTransaction transaction) : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
