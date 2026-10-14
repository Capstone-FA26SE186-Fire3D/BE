using Fire3D.Application.Administration;
using Fire3D.Domain.Entities;

namespace Fire3D.Application.Buildings;

public interface IBuildingStore
{
    Task<Building?> FindBuildingAsync(Guid id, Guid? organizationId, CancellationToken ct);
    Task<PageResponse<BuildingSummaryResponse>> ListBuildingsAsync(Guid? organizationId, BuildingFilter filter, CancellationToken ct);
    Task<bool> TryCreateBuildingAsync(Building building, BuildingLocation? location, BuildingContact? contact, CancellationToken ct);
    Task<bool> CreateBuildingWithAuditAsync(Building building, BuildingLocation? location, BuildingContact? contact, Guid actorId, DateTime now, CancellationToken ct);
    /// <summary>Creates the Building once per actor/Idempotency-Key; a repeated key returns the first Building or KeyConflict.</summary>
    Task<(BuildingWriteOutcome Outcome, Guid BuildingId)> CreateBuildingOnceAsync(Building building, BuildingLocation? location, BuildingContact? contact,
        Guid actorId, DateTime now, string idempotencyKey, string inputHash, CancellationToken ct);
    Task UpdateBuildingAsync(Building building, BuildingLocation? location, BuildingContact? contact, CancellationToken ct);
    /// <summary>expectedVersion is the If-Match version; it is compared under the Building lock (Stale on mismatch).</summary>
    Task<BuildingWriteOutcome> UpdateBuildingWithAuditAsync(Building building, BuildingLocation? location, BuildingContact? contact, Guid actorId, DateTime now, CancellationToken ct, long? expectedVersion = null);
    Task SetBuildingActiveAsync(Guid id, Guid organizationId, bool active, DateTime now, CancellationToken ct);
    Task<BuildingWriteOutcome> SetBuildingActiveWithAuditAsync(Guid id, Guid organizationId, bool active, Guid actorId, DateTime now, CancellationToken ct, long? expectedVersion = null);
    Task<bool> TryCreateRevisionAsync(Revision revision, SourceDocument document, ProcessingJob job, CancellationToken ct);
    Task<bool> RevisionBuildingExistsAsync(Guid buildingId, Guid? organizationId, CancellationToken ct);
    Task<PageResponse<RevisionResponse>> ListRevisionsAsync(Guid buildingId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<RevisionResponse?> FindRevisionAsync(Guid revisionId, Guid? organizationId, CancellationToken ct);
    Task WriteAuditAsync(Guid actorId, Guid organizationId, string targetEntity, Guid targetId, string action, DateTime now, CancellationToken ct);
}
