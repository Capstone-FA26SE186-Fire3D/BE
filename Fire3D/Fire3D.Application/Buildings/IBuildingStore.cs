using Fire3D.Application.Administration;
using Fire3D.Domain.Entities;

namespace Fire3D.Application.Buildings;

public interface IBuildingStore
{
    Task<Building?> FindBuildingAsync(Guid id, Guid organizationId, CancellationToken ct);
    Task<PageResponse<BuildingSummaryResponse>> ListBuildingsAsync(Guid organizationId, BuildingFilter filter, CancellationToken ct);
    Task<bool> TryCreateBuildingAsync(Building building, BuildingLocation? location, BuildingContact? contact, CancellationToken ct);
    Task UpdateBuildingAsync(Building building, BuildingLocation? location, BuildingContact? contact, CancellationToken ct);
    Task SetBuildingActiveAsync(Guid id, Guid organizationId, bool active, DateTime now, CancellationToken ct);
    Task<bool> TryCreateRevisionAsync(Revision revision, SourceDocument document, ProcessingJob job, CancellationToken ct);
    Task<IReadOnlyList<RevisionResponse>> ListRevisionsAsync(Guid buildingId, Guid organizationId, CancellationToken ct);
    Task<RevisionResponse?> FindRevisionAsync(Guid revisionId, Guid organizationId, CancellationToken ct);
    Task WriteAuditAsync(Guid actorId, Guid organizationId, string targetEntity, Guid targetId, string action, DateTime now, CancellationToken ct);
}
