using Fire3D.Application.Ifc;
using Fire3D.Application.Administration;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    public Task<PageResponse<RevisionIssueResponse>?> ListRevisionIssuesAsync(Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct) =>
        ReadRevisionPageAsync<RevisionIssueResponse>("""
            SELECT i.id,i.created_at,jsonb_build_object('id',i.id,'revisionId',i.revision_id,
                'validationRunId',i.validation_run_id,'processingAttemptId',v.processing_attempt_id,
                'artifactId',i.artifact_id,'issueCode',i.issue_code,'severity',i.severity,'status',i.status,
                'message',i.message,'evidence',i.evidence,'isCurrentAttempt',v.processing_attempt_id=j.current_attempt_id,
                'createdAt',i.created_at) AS item
            FROM public.validation_issues i JOIN scoped_revision r ON r.id=i.revision_id
            JOIN public.validation_runs v ON v.id=i.validation_run_id AND v.revision_id=i.revision_id
            JOIN public.processing_jobs j ON j.id=v.processing_job_id AND j.revision_id=v.revision_id
            JOIN public.processing_job_attempts a ON a.id=v.processing_attempt_id AND a.processing_job_id=j.id AND a.input_hash=j.input_hash
            """, revisionId, organizationId, page, pageSize, ct);
}
