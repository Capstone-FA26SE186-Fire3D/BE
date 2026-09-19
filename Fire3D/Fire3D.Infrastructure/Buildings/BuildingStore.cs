using Fire3D.Application.Administration;
using Fire3D.Application.Buildings;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Buildings;

public sealed class BuildingStore(Fire3DDbContext db) : IBuildingStore
{
    public Task<Building?> FindBuildingAsync(Guid id, Guid organizationId, CancellationToken ct) =>
        db.Buildings
            .Include(x => x.BuildingLocation)
            .Include(x => x.BuildingContact)
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId && x.DeletedAt == null, ct);

    public async Task<PageResponse<BuildingSummaryResponse>> ListBuildingsAsync(Guid organizationId, BuildingFilter filter, CancellationToken ct)
    {
        var query = db.Buildings.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.DeletedAt == null);
        
        if (filter.IsActive.HasValue) query = query.Where(x => x.IsActive == filter.IsActive.Value);
        
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Name.ToLower().Contains(search) || (x.BuildingType != null && x.BuildingType.ToLower().Contains(search)));
        }

        var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(x => new BuildingSummaryResponse(x.Id, x.Name, x.BuildingType, x.TotalFloors, x.IsActive, x.CreatedAt))
            .ToListAsync(ct);
            
        return new PageResponse<BuildingSummaryResponse>(items, count, filter.Page, filter.PageSize);
    }

    public async Task<bool> TryCreateBuildingAsync(Building building, BuildingLocation? location, BuildingContact? contact, CancellationToken ct)
    {
        db.Buildings.Add(building);
        if (location != null) db.Set<BuildingLocation>().Add(location);
        if (contact != null) db.Set<BuildingContact>().Add(contact);
        
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpdateBuildingAsync(Building building, BuildingLocation? location, BuildingContact? contact, CancellationToken ct)
    {
        var existing = await db.Buildings
            .Include(x => x.BuildingLocation)
            .Include(x => x.BuildingContact)
            .FirstOrDefaultAsync(x => x.Id == building.Id, ct);
            
        if (existing == null) return;

        existing.Name = building.Name;
        existing.BuildingType = building.BuildingType;
        existing.TotalFloors = building.TotalFloors;
        existing.UpdatedAt = building.UpdatedAt;
        
        if (location != null) 
        {
            if (existing.BuildingLocation != null)
            {
                existing.BuildingLocation.Address = location.Address;
                existing.BuildingLocation.City = location.City;
                existing.BuildingLocation.District = location.District;
                existing.BuildingLocation.Latitude = location.Latitude;
                existing.BuildingLocation.Longitude = location.Longitude;
                existing.BuildingLocation.Geojson = location.Geojson;
                existing.BuildingLocation.UpdatedAt = location.UpdatedAt;
            }
            else
            {
                existing.BuildingLocation = location;
            }
        }
        
        if (contact != null)
        {
            if (existing.BuildingContact != null)
            {
                existing.BuildingContact.ContactName = contact.ContactName;
                existing.BuildingContact.ContactRole = contact.ContactRole;
                existing.BuildingContact.Phone = contact.Phone;
                existing.BuildingContact.Email = contact.Email;
                existing.BuildingContact.IsPrimary = contact.IsPrimary;
                existing.BuildingContact.UpdatedAt = contact.UpdatedAt;
            }
            else
            {
                existing.BuildingContact = contact;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SetBuildingActiveAsync(Guid id, Guid organizationId, bool active, DateTime now, CancellationToken ct)
    {
        await db.Buildings
            .Where(x => x.Id == id && x.OrganizationId == organizationId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.IsActive, active)
                .SetProperty(x => x.UpdatedAt, now), ct);
    }

    public async Task<bool> TryCreateRevisionAsync(Revision revision, SourceDocument document, ProcessingJob job, CancellationToken ct)
    {
        db.Revisions.Add(revision);
        db.SourceDocuments.Add(document);
        db.ProcessingJobs.Add(job);
        
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<RevisionResponse>> ListRevisionsAsync(Guid buildingId, Guid organizationId, CancellationToken ct)
    {
        return await db.Revisions.AsNoTracking()
            .Include(x => x.SourceDocument)
            .Where(x => x.BuildingId == buildingId && x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new RevisionResponse(
                x.Id,
                x.BuildingId,
                x.VersionLabel,
                x.Status.ToString(),
                x.CreatedAt,
                x.SourceDocument != null ? new SourceDocumentResponse(x.SourceDocument.Id, x.SourceDocument.OriginalFilename, x.SourceDocument.FileSizeBytes, x.SourceDocument.QuarantineStatus.ToString(), x.SourceDocument.CreatedAt) : null
            ))
            .ToListAsync(ct);
    }

    public async Task<RevisionResponse?> FindRevisionAsync(Guid revisionId, Guid organizationId, CancellationToken ct)
    {
        return await db.Revisions.AsNoTracking()
            .Include(x => x.SourceDocument)
            .Where(x => x.Id == revisionId && x.OrganizationId == organizationId)
            .Select(x => new RevisionResponse(
                x.Id,
                x.BuildingId,
                x.VersionLabel,
                x.Status.ToString(),
                x.CreatedAt,
                x.SourceDocument != null ? new SourceDocumentResponse(x.SourceDocument.Id, x.SourceDocument.OriginalFilename, x.SourceDocument.FileSizeBytes, x.SourceDocument.QuarantineStatus.ToString(), x.SourceDocument.CreatedAt) : null
            ))
            .SingleOrDefaultAsync(ct);
    }

    public async Task WriteAuditAsync(Guid actorId, Guid organizationId, string targetEntity, Guid targetId, string action, DateTime now, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = actorId,
            OrganizationId = organizationId,
            ActorType = "User",
            Action = Enum.Parse<AuditAction>(action),
            TargetEntity = targetEntity,
            TargetId = targetId,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
    }
}
