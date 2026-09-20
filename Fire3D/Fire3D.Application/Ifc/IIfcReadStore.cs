using Fire3D.Application.Administration;
namespace Fire3D.Application.Ifc;
public interface IIfcReadStore
{
    Task<PageResponse<BimFactResponse>?> ListBimFactsAsync(Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<PageResponse<RevisionArtifactResponse>?> ListRevisionArtifactsAsync(Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<PageResponse<RevisionIssueResponse>?> ListRevisionIssuesAsync(Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<JobQaResponse?> GetJobQaAsync(Guid jobId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
    Task<ValidationRunResponse?> GetValidationAsync(Guid validationRunId, Guid? organizationId, CancellationToken ct);
    Task<ProcessingJobDetailResponse?> GetJobAsync(Guid jobId, Guid? organizationId, CancellationToken ct);
    Task<PageResponse<ProcessingJobResponse>?> ListJobsAsync(Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct);
}
