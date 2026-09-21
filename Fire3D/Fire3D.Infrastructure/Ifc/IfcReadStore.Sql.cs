using System.Data;
using System.Text.Json;
using Fire3D.Application.Ifc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ScopeJoins = """
        JOIN public.revisions r ON r.id = j.revision_id
        JOIN public.buildings b ON b.id = r.building_id
        JOIN public.organizations o ON o.id = b.organization_id
        """;
    private const string ScopePredicate = """
        b.deleted_at IS NULL AND b.is_active AND o.deleted_at IS NULL AND o.is_active
        AND (@tenant IS NULL OR b.organization_id = @tenant)
        """;

    // All SQL is owned by this store. Resource IDs, scope and pagination are parameters.
    private async Task<T?> ReadJsonAsync<T>(string sql, Guid id, Guid? tenant, CancellationToken ct,
        int page = 1, int pageSize = 20) where T : class
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = sql;
            command.CommandTimeout = 30;
            Add("id", DbType.Guid, id);
            Add("tenant", DbType.Guid, tenant.HasValue ? tenant.Value : DBNull.Value);
            Add("offset", DbType.Int32, (page - 1) * pageSize);
            Add("limit", DbType.Int32, pageSize);
            Add("page", DbType.Int32, page);
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? null : JsonSerializer.Deserialize<T>((string)value, JsonOptions);
            void Add(string name, DbType type, object value)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name; parameter.DbType = type; parameter.Value = value;
                command.Parameters.Add(parameter);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public Task<ProcessingJobDetailResponse?> GetJobAsync(Guid jobId, Guid? organizationId, CancellationToken ct) =>
        ReadJsonAsync<ProcessingJobDetailResponse>("""
            SELECT jsonb_build_object(
                'job', jsonb_build_object('id',j.id,'revisionId',j.revision_id,'sourceDocumentId',j.source_document_id,
                    'scenarioVersionId',j.scenario_version_id,'kind',j.kind,'status',j.status,'createdAt',j.created_at),
                'inputHash',j.input_hash,'currentAttemptId',j.current_attempt_id,
                'currentAttempt', CASE WHEN a.id IS NULL THEN NULL ELSE jsonb_build_object(
                    'id',a.id,'attemptNumber',a.attempt_number,'status',a.status,'toolchainVersion',a.toolchain_version,
                    'startedAt',a.started_at,'finishedAt',a.finished_at,'outputHash',a.output_hash) END)::text
            FROM public.processing_jobs j
            """ + "\n" + ScopeJoins + "\n" + """
            LEFT JOIN public.processing_job_attempts a ON a.id=j.current_attempt_id
                AND a.processing_job_id=j.id AND a.input_hash=j.input_hash
            WHERE j.id=@id AND
            """ + " " + ScopePredicate, jobId, organizationId, ct);
}
