using Fire3D.Application.Administration;
namespace Fire3D.Application.Ifc;
public interface IIfcReadStore
{
    Task<PageResponse<ProcessingJobResponse>?> ListJobsAsync(Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
}
