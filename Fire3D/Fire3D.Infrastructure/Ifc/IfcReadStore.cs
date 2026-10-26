using Fire3D.Application.Administration;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore(Fire3DDbContext db) : IIfcReadStore
{
    private IQueryable<Fire3D.Domain.Entities.Revision> Revisions(Guid? tenant) =>
        db.Revisions.AsNoTracking().Where(r => db.Buildings.Any(b => b.Id == r.BuildingId
            && b.DeletedAt == null && b.IsActive && b.Organization.DeletedAt == null && b.Organization.IsActive
            && (tenant == null || b.OrganizationId == tenant)));

    public async Task<PageResponse<ProcessingJobResponse>?> ListJobsAsync(
        Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct)
    {
        // Existence, total and page in one round trip: no visible revision row means 404.
        var result = await Revisions(organizationId).Where(r => r.Id == revisionId)
            .Select(r => new
            {
                Total = db.ProcessingJobs.Count(j => j.RevisionId == r.Id),
                Items = db.ProcessingJobs.Where(j => j.RevisionId == r.Id)
                    .OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id)
                    .Skip((page - 1) * pageSize).Take(pageSize)
                    .Select(j => new ProcessingJobResponse(j.Id, j.RevisionId, j.SourceDocumentId,
                        j.ScenarioVersionId, j.Kind, j.Status, j.CreatedAt)).ToList()
            })
            .SingleOrDefaultAsync(ct);
        return result is null ? null : new(result.Items, result.Total, page, pageSize);
    }
}
