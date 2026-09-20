using Fire3D.Application.Ifc;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    private const string ValidationJson = """
        jsonb_build_object('id',v.id,'revisionId',v.revision_id,'processingJobId',v.processing_job_id,
            'processingAttemptId',v.processing_attempt_id,'artifactId',v.artifact_id,
            'scenarioVersionId',v.scenario_version_id,'scope',v.scope,'validatorVersion',v.validator_version,
            'status',v.status,'summary',v.summary,'startedAt',v.started_at,'finishedAt',v.finished_at,'createdAt',v.created_at)
        """;
    public Task<ValidationRunResponse?> GetValidationAsync(Guid validationRunId, Guid? organizationId, CancellationToken ct) =>
        ReadJsonAsync<ValidationRunResponse>("SELECT " + ValidationJson + "::text FROM public.validation_runs v " +
            "JOIN public.processing_jobs j ON j.id=v.processing_job_id AND j.revision_id=v.revision_id " +
            "JOIN public.processing_job_attempts a ON a.id=v.processing_attempt_id AND a.processing_job_id=j.id AND a.input_hash=j.input_hash " +
            ScopeJoins + " WHERE v.id=@id AND " + ScopePredicate, validationRunId, organizationId, ct);
}
