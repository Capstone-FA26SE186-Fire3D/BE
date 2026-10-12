using Fire3D.Application.Ifc;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    private const string ValidationJson = """
        jsonb_build_object('id',v.id,'revisionId',v.revision_id,'processingJobId',v.processing_job_id,
            'processingAttemptId',v.processing_attempt_id,'artifactId',v.artifact_id,
            'scenarioVersionId',v.scenario_version_id,'scope',v.scope,'validatorVersion',v.validator_version,
            'status',v.status,'outcome',v.outcome,'summary',v.summary,'startedAt',v.started_at,'finishedAt',v.finished_at,'createdAt',v.created_at)
        """;
    public Task<Fire3D.Application.Administration.PageResponse<ValidationRunHistoryItem>?> ListValidationRunsAsync(Guid revisionId, Guid? jobId,
        Guid? organizationId, int page, int pageSize, CancellationToken ct) =>
        // History keeps every run of the revision; only the accepted current attempt of a Succeeded job is marked current.
        // jobId is a Guid value, so formatting it into the filter cannot inject SQL.
        ReadRevisionPageAsync<ValidationRunHistoryItem>("""
            SELECT v.id,v.created_at,jsonb_build_object('id',v.id,'revisionId',v.revision_id,'processingJobId',v.processing_job_id,'jobKind',j.kind,
                'processingAttemptId',v.processing_attempt_id,'attemptStatus',a.status,'artifactId',v.artifact_id,'scenarioVersionId',v.scenario_version_id,
                'scope',v.scope,'validatorVersion',v.validator_version,'outcome',v.outcome,
                'isCurrent',a.id=j.current_attempt_id AND a.status='Succeeded' AND j.status='Succeeded' AND a.input_hash=j.input_hash,
                'startedAt',v.started_at,'finishedAt',v.finished_at,'createdAt',v.created_at) AS item
            FROM public.validation_runs v JOIN scoped_revision r ON r.id=v.revision_id
            JOIN public.processing_jobs j ON j.id=v.processing_job_id AND j.revision_id=v.revision_id
            JOIN public.processing_job_attempts a ON a.id=v.processing_attempt_id AND a.processing_job_id=j.id
            """ + (jobId is Guid job ? $" WHERE j.id='{job:D}'::uuid" : ""), revisionId, organizationId, page, pageSize, ct);

    public Task<ValidationRunResponse?> GetValidationAsync(Guid validationRunId, Guid? organizationId, CancellationToken ct) =>
        ReadJsonAsync<ValidationRunResponse>("SELECT " + ValidationJson + "::text FROM public.validation_runs v " +
            "JOIN public.processing_jobs j ON j.id=v.processing_job_id AND j.revision_id=v.revision_id " +
            "JOIN public.processing_job_attempts a ON a.id=v.processing_attempt_id AND a.processing_job_id=j.id AND a.input_hash=j.input_hash AND a.status='Succeeded' AND j.status='Succeeded' AND a.id=j.current_attempt_id " +
            ScopeJoins + " WHERE v.id=@id AND " + ScopePredicate, validationRunId, organizationId, ct);
}
