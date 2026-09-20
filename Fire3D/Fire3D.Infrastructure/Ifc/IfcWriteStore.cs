using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcWriteStore(Fire3DDbContext db) : IIfcWriteStore
{
    private async Task<object?> ScalarAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        cmd.CommandTimeout = 30;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync(ct);
    }
    // Locks identity and tenant rows, not the job. The requeue gate must acquire its event-key lock before the job.
    private const string JobScopeSql = """
        SELECT b.organization_id FROM public.processing_jobs j
        JOIN public.revisions r ON r.id=j.revision_id
        JOIN public.buildings b ON b.id=r.building_id
        JOIN public.organizations o ON o.id=b.organization_id
        JOIN public.users u ON u.id=@actor
        WHERE j.id=@job AND u.is_active AND u.deleted_at IS NULL
          AND b.is_active AND b.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL
          AND ((u.role='PlatformAdmin' AND u.organization_id IS NULL)
            OR (u.role='OrganizationUser' AND u.organization_id=b.organization_id))
        FOR SHARE OF u,o,b
        """;

    public async Task<AuthResult<RetryProcessingJobResponse>> RetryJobAsync(
        Guid actorId, Guid jobId, Guid requestId, string reason, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tenant = await ScalarAsync(JobScopeSql, ct, ("actor",actorId), ("job",jobId));
        if (tenant is not Guid organizationId)
            return AuthResult<RetryProcessingJobResponse>.Fail("NOT_FOUND", "Processing job is unavailable in this scope.", 404);
        string key = $"ifc-retry:{actorId:N}:{requestId:N}";
        string outcome;
        try
        {
            outcome = (string)(await ScalarAsync("SELECT public.requeue_processing_job(@job,@key,@reason)",
                ct, ("job",jobId), ("key",key), ("reason",reason)))!;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.RaiseException
            && ex.MessageText == "requeue idempotency key conflicts with a different envelope")
        {
            return AuthResult<RetryProcessingJobResponse>.Fail("IDEMPOTENCY_CONFLICT", "requestId was already used with different input.", 409);
        }
        if (outcome is "Conflict" or "NotClaimable")
            return AuthResult<RetryProcessingJobResponse>.Fail(outcome == "Conflict" ? "JOB_CONFLICT" : "JOB_NOT_CLAIMABLE",
                "Only a failed job can be requeued with a new requestId.", 409);
        if (outcome is not ("Requeued" or "AlreadyRequeued"))
            throw new InvalidOperationException("Unexpected processing gate result.");
        if (outcome == "Requeued")
            await ScalarAsync("""
                INSERT INTO public.audit_logs(user_id,organization_id,actor_type,action,target_entity,target_id,new_values)
                VALUES (@actor,@tenant,'User','Update','processing_jobs',@job,
                    jsonb_build_object('operation','Retry','requestId',@request,'reason',@reason))
                RETURNING id
                """,ct,("actor",actorId),("tenant",organizationId),("job",jobId),("request",requestId),("reason",reason));
        await transaction.CommitAsync(ct);
        return AuthResult<RetryProcessingJobResponse>.Ok(new(jobId,outcome));
    }
}
