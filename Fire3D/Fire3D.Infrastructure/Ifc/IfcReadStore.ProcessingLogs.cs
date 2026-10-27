using Fire3D.Application.Administration;
using Fire3D.Application.Ifc;

namespace Fire3D.Infrastructure.Ifc;

public sealed partial class IfcReadStore
{
    // One entry per worker attempt of the revision's processing jobs: step is the job kind, status the attempt status, message
    // its error. revision_processing_logs never had a writer and is being dropped.
    public Task<PageResponse<RevisionProcessingLogResponse>?> ListRevisionProcessingLogsAsync(
        Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct) =>
        ReadRevisionPageAsync<RevisionProcessingLogResponse>("""
            SELECT a.id,a.started_at AS created_at,jsonb_build_object(
                'id',a.id,'revisionId',j.revision_id,'jobId',j.id,
                'step',j.kind,'status',a.status,'message',a.error_message,
                'durationMs',CASE WHEN a.finished_at IS NULL THEN NULL ELSE least(floor(extract(epoch FROM a.finished_at-a.started_at)*1000),2147483647)::int END,
                'attemptNumber',a.attempt_number,'loggedAt',COALESCE(a.finished_at,a.started_at)) AS item
            FROM public.processing_job_attempts a
            JOIN public.processing_jobs j ON j.id=a.processing_job_id
            JOIN scoped_revision r ON r.id=j.revision_id
            """, revisionId, organizationId, page, pageSize, ct);
}
