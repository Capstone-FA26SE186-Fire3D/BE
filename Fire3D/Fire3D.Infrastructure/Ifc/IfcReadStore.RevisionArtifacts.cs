using Fire3D.Application.Ifc;
using Fire3D.Application.Administration;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    public Task<PageResponse<RevisionArtifactResponse>?> ListRevisionArtifactsAsync(Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct) =>
        ReadRevisionPageAsync<RevisionArtifactResponse>("""
            SELECT x.id,x.created_at,jsonb_build_object('id',x.id,'revisionId',x.revision_id,'jobId',x.job_id,
                'attemptId',x.attempt_id,'artifactType',x.artifact_type,'sha256Hash',x.sha256_hash,
                'metadata',x.metadata,'isRuntimeReady',x.is_runtime_ready,'isCurrentAttempt',x.attempt_id=j.current_attempt_id,
                'createdAt',x.created_at) AS item
            FROM public.revision_artifacts x JOIN scoped_revision r ON r.id=x.revision_id
            JOIN public.processing_jobs j ON j.id=x.job_id AND j.revision_id=x.revision_id
            JOIN public.processing_job_attempts a ON a.id=x.attempt_id AND a.processing_job_id=j.id AND a.input_hash=j.input_hash
            """, revisionId, organizationId, page, pageSize, ct);
}
