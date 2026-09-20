using Fire3D.Application.Administration;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore(Fire3DDbContext db) : IIfcReadStore
{
    private IQueryable<Fire3D.Domain.Entities.Revision> Revisions(Guid? tenant) =>
        db.Revisions.AsNoTracking().Where(r => r.Building.DeletedAt == null && r.Building.IsActive
            && r.Building.Organization.DeletedAt == null && r.Building.Organization.IsActive
            && (tenant == null || r.Building.OrganizationId == tenant));

    public async Task<PageResponse<ProcessingJobResponse>?> ListJobsAsync(
        Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct)
    {
        var revisions = Revisions(organizationId).Where(r => r.Id == revisionId);
        if (!await revisions.AnyAsync(ct)) return null;
        var query = db.ProcessingJobs.AsNoTracking().Where(j => revisions.Any(r => r.Id == j.RevisionId));
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(j => new ProcessingJobResponse(j.Id, j.RevisionId, j.SourceDocumentId,
                j.ScenarioVersionId, j.Kind, j.Status, j.CreatedAt)).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }
}
