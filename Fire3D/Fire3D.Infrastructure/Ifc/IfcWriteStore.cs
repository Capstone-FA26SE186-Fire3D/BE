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

    public async Task<AuthResult<bool>> InitiateUploadAsync(Guid actorId, Guid buildingId, Guid revisionId, string versionLabel, Guid? actorTenantId, CancellationToken ct)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Id == buildingId && b.IsActive && b.DeletedAt == null, ct);
        if (building == null || (actorTenantId.HasValue && building.OrganizationId != actorTenantId.Value))
            return AuthResult<bool>.Fail("NOT_FOUND", "Building not found or access denied.", 404);

        var revision = new Fire3D.Domain.Entities.Revision
        {
            Id = revisionId,
            BuildingId = buildingId,
            OrganizationId = building.OrganizationId,
            UploadedBy = actorId,
            VersionLabel = versionLabel,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Revisions.Add(revision);
        await db.SaveChangesAsync(ct);
        return AuthResult<bool>.Ok(true);
    }

    public async Task<AuthResult<bool>> FinalizeUploadAsync(Guid actorId, Guid revisionId, Fire3D.Application.Ifc.Commands.FinalizeUpload.FinalizeIfcUploadRequest request, Guid? actorTenantId, CancellationToken ct)
    {
        var revision = await db.Revisions.FirstOrDefaultAsync(r => r.Id == revisionId, ct);
        if (revision == null || (actorTenantId.HasValue && revision.OrganizationId != actorTenantId.Value))
            return AuthResult<bool>.Fail("NOT_FOUND", "Revision not found or access denied.", 404);

        // Check if SourceDocument already exists
        var existingSource = await db.SourceDocuments.FirstOrDefaultAsync(s => s.RevisionId == revisionId, ct);
        if (existingSource != null)
            return AuthResult<bool>.Fail("CONFLICT", "Upload already finalized for this revision.", 409);

        var sourceDoc = new Fire3D.Domain.Entities.SourceDocument
        {
            Id = Guid.NewGuid(),
            RevisionId = revisionId,
            UploadedBy = actorId,
            OriginalFilename = request.OriginalFilename,
            FileSizeBytes = request.FileSizeBytes,
            StorageUrl = request.ObjectKey,
            MimeType = request.MimeType,
            Sha256Hash = request.Sha256Hash,
            UsageRights = "Private", // Standard default
            CreatedAt = DateTime.UtcNow
        };
        db.SourceDocuments.Add(sourceDoc);
        
        await db.SaveChangesAsync(ct);
        return AuthResult<bool>.Ok(true);
    }

    public async Task<AuthResult<Guid>> ProcessRevisionAsync(Guid actorId, Guid revisionId, Guid? actorTenantId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var revision = await db.Revisions.Include(r => r.SourceDocument)
            .FirstOrDefaultAsync(r => r.Id == revisionId, ct);
            
        if (revision == null || (actorTenantId.HasValue && revision.OrganizationId != actorTenantId.Value))
            return AuthResult<Guid>.Fail("NOT_FOUND", "Revision not found or access denied.", 404);

        if (revision.SourceDocument == null)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "Revision does not have a source document to process.", 400);

        // Check if there is an existing job
        var existingJob = await db.ProcessingJobs.FirstOrDefaultAsync(j => j.RevisionId == revisionId, ct);
        if (existingJob != null)
            return AuthResult<Guid>.Fail("CONFLICT", "Processing job already exists for this revision.", 409);

        var jobId = Guid.NewGuid();
        var job = new Fire3D.Domain.Entities.ProcessingJob
        {
            Id = jobId,
            RevisionId = revisionId,
            SourceDocumentId = revision.SourceDocument.Id,
            Kind = "ProcessIfc",
            JobKey = Guid.NewGuid(),
            Status = "Queued",
            AttemptNumber = 0,
            ToolchainVersion = "v1", // Default toolchain
            CreatedAt = DateTime.UtcNow
        };
        db.ProcessingJobs.Add(job);
        
        var audit = new Fire3D.Domain.Entities.AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = actorId,
            OrganizationId = revision.OrganizationId,
            ActorType = "User",
            Action = Fire3D.Domain.Enums.AuditAction.Create,
            TargetEntity = "ProcessingJob",
            TargetId = jobId,
            CorrelationId = Guid.NewGuid(),
            NewValues = $$"""{"status": "Queued", "revision_id": "{{revisionId}}"}""",
            CreatedAt = DateTime.UtcNow
        };
        db.AuditLogs.Add(audit);
        
        await db.SaveChangesAsync(ct);

        // Enqueue outbox event for Worker to pick up
        var payload = $$"""{"job_id": "{{jobId}}", "revision_id": "{{revisionId}}", "source_url": "{{revision.SourceDocument.StorageUrl}}"}""";
        await ScalarAsync("""
            INSERT INTO public.integration_outbox_events(id, aggregate_type, aggregate_id, event_type, payload, created_at)
            VALUES (@id, 'ProcessingJob', @job, 'ProcessingJobRequested', @payload::jsonb, now())
            """, ct, ("id", Guid.NewGuid()), ("job", jobId), ("payload", payload));

        await transaction.CommitAsync(ct);
        return AuthResult<Guid>.Ok(jobId);
    }
}
