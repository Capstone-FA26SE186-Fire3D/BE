using Fire3D.Application.Ifc;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    public Task<JobQaResponse?> GetJobQaAsync(Guid jobId, Guid? organizationId, int page, int pageSize, CancellationToken ct) =>
        ReadJsonAsync<JobQaResponse>(
            "WITH scoped_job AS (SELECT j.id,j.revision_id,j.current_attempt_id,j.input_hash FROM public.processing_jobs j " +
            ScopeJoins + " WHERE j.id=@id AND " + ScopePredicate + "), runs AS (SELECT " + ValidationJson +
            " AS item,v.created_at,v.id FROM public.validation_runs v JOIN scoped_job j ON j.id=v.processing_job_id " +
            "AND j.revision_id=v.revision_id AND j.current_attempt_id=v.processing_attempt_id " +
            "JOIN public.processing_job_attempts a ON a.id=v.processing_attempt_id AND a.processing_job_id=j.id AND a.input_hash=j.input_hash) " +
            """
            SELECT jsonb_build_object('jobId',j.id,'currentAttemptId',j.current_attempt_id,
                'validationRuns',jsonb_build_object('items',COALESCE((SELECT jsonb_agg(p.item ORDER BY p.created_at DESC,p.id)
                    FROM (SELECT * FROM runs ORDER BY created_at DESC,id LIMIT @limit OFFSET @offset) p),'[]'::jsonb),
                    'totalCount',(SELECT count(*) FROM runs),'page',@page,'pageSize',@limit))::text
            FROM scoped_job j
            """,jobId,organizationId,ct,page,pageSize);
}
