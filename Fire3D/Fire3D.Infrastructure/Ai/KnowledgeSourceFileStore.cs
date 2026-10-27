using Fire3D.Application.Ai;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Ai;

/// <summary>knowledge_source_files (BE#179), read and written by the API role; a stored file is immutable (trigger).</summary>
public sealed class KnowledgeSourceFileStore(Fire3DDbContext db) : IKnowledgeSourceFileStore
{
    private const string Columns = """
        id AS "Id", created_by AS "CreatedBy", input_hash AS "InputHash", file_name AS "FileName", content_type AS "ContentType",
        size_bytes AS "SizeBytes", staging_key AS "StagingKey", object_key AS "ObjectKey", sha256 AS "Sha256", source_id AS "SourceId",
        created_at AS "CreatedAt", expires_at AS "ExpiresAt", completed_at AS "CompletedAt"
        """;
    // Constant SQL; values are always bound as parameters ({0}, {1}).
    private const string SelectByKey = "SELECT " + Columns + " FROM public.knowledge_source_files WHERE created_by={0} AND idempotency_key={1}";
    private const string SelectById = "SELECT " + Columns + " FROM public.knowledge_source_files WHERE id={0}";

    public async Task<KnowledgeSourceFileRecord> CreateOrGetIntentAsync(KnowledgeSourceFileRecord c, string idempotencyKey, CancellationToken ct)
    {
        // The first request under (actor, key) inserts; any later one, even concurrent, reads that row back.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.knowledge_source_files(id,created_by,idempotency_key,input_hash,file_name,content_type,size_bytes,staging_key,created_at,expires_at)
            VALUES({c.Id},{c.CreatedBy},{idempotencyKey},{c.InputHash},{c.FileName},{c.ContentType},{c.SizeBytes},{c.StagingKey},{c.CreatedAt},{c.ExpiresAt})
            ON CONFLICT (created_by,idempotency_key) DO NOTHING
            """, ct);
        return await db.Database.SqlQueryRaw<KnowledgeSourceFileRecord>(SelectByKey, c.CreatedBy, idempotencyKey).SingleAsync(ct);
    }

    public Task<KnowledgeSourceFileRecord?> FindAsync(Guid id, CancellationToken ct) =>
        db.Database.SqlQueryRaw<KnowledgeSourceFileRecord>(SelectById, id).SingleOrDefaultAsync(ct);

    public async Task<bool> CompleteAsync(Guid id, string objectKey, string sha256, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.knowledge_source_files SET object_key={objectKey},sha256={sha256},completed_at=clock_timestamp()
            WHERE id={id} AND completed_at IS NULL
            """, ct) == 1;

    public Task LinkSourceAsync(Guid id, Guid sourceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.knowledge_source_files SET source_id={sourceId} WHERE id={id} AND source_id IS NULL", ct);
}
