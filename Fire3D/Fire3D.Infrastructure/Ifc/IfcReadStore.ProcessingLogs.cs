using Fire3D.Application.Administration;
using Fire3D.Application.Ifc;

namespace Fire3D.Infrastructure.Ifc;

public sealed partial class IfcReadStore
{
    public Task<PageResponse<RevisionProcessingLogResponse>?> ListRevisionProcessingLogsAsync(
        Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct) =>
        ReadRevisionPageAsync<RevisionProcessingLogResponse>("""
            SELECT l.id,l.logged_at,jsonb_build_object(
                'id',l.id,'revisionId',l.revision_id,'jobId',l.job_id,
                'step',l.step,'status',l.status,'message',l.message,
                'durationMs',l.duration_ms,'attemptNumber',l.attempt_number,'loggedAt',l.logged_at) AS item
            FROM public.revision_processing_logs l
            JOIN scoped_revision r ON r.id=l.revision_id
            """, revisionId, organizationId, page, pageSize, ct);
}
