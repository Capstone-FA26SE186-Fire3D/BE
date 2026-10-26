using Fire3D.Application.Content;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Fire3D.Infrastructure.Content;

/// <summary>learn_media_images (BE#171), read and written by the API role; a completed row is immutable (trigger).</summary>
public sealed class LearnMediaStore(Fire3DDbContext db) : ILearnMediaStore
{
    private const string Columns = """
        id AS "Id", created_by AS "CreatedBy", input_hash AS "InputHash", file_name AS "FileName", content_type AS "ContentType",
        size_bytes AS "SizeBytes", sha256 AS "Sha256", staging_key AS "StagingKey", object_key AS "ObjectKey", width AS "Width",
        height AS "Height", created_at AS "CreatedAt", expires_at AS "ExpiresAt", completed_at AS "CompletedAt"
        """;
    // Constant SQL; values are always bound as parameters ({0}, {1}).
    private const string SelectByKey = "SELECT " + Columns + " FROM public.learn_media_images WHERE created_by={0} AND idempotency_key={1}";
    private const string SelectById = "SELECT " + Columns + " FROM public.learn_media_images WHERE id={0}";

    public async Task<LearnImageRecord> CreateOrGetIntentAsync(LearnImageRecord c, string idempotencyKey, CancellationToken ct)
    {
        // The first request under (actor, key) inserts; any later one, even concurrent, reads that row back.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.learn_media_images(id,created_by,idempotency_key,input_hash,file_name,content_type,size_bytes,sha256,staging_key,created_at,expires_at)
            VALUES({c.Id},{c.CreatedBy},{idempotencyKey},{c.InputHash},{c.FileName},{c.ContentType},{c.SizeBytes},{c.Sha256},{c.StagingKey},{c.CreatedAt},{c.ExpiresAt})
            ON CONFLICT (created_by,idempotency_key) DO NOTHING
            """, ct);
        return await db.Database.SqlQueryRaw<LearnImageRecord>(SelectByKey, c.CreatedBy, idempotencyKey).SingleAsync(ct);
    }

    public Task<LearnImageRecord?> FindAsync(Guid id, CancellationToken ct) =>
        db.Database.SqlQueryRaw<LearnImageRecord>(SelectById, id).SingleOrDefaultAsync(ct);

    public async Task<bool> CompleteAsync(Guid id, string objectKey, int width, int height, CancellationToken ct)
    {
        // Completion and its audit commit together. The data-modifying WITH must run as its own statement (EF would wrap it), and the
        // audit insert returns nothing because the API role cannot read audit_logs.
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH done AS (
              UPDATE public.learn_media_images SET object_key=@object_key,width=@width,height=@height,completed_at=clock_timestamp()
              WHERE id=@id AND completed_at IS NULL
              RETURNING id,created_by,content_type,size_bytes,sha256,width,height,completed_at),
            audit AS (
              INSERT INTO public.audit_logs(id,user_id,organization_id,actor_type,action,target_entity,target_id,correlation_id,new_values,created_at)
              SELECT gen_random_uuid(),created_by,NULL,'User','Create'::audit_action_enum,'LearnMediaImage',id,gen_random_uuid(),
                jsonb_build_object('contentType',content_type,'sizeBytes',size_bytes,'sha256',sha256,'width',width,'height',height),completed_at
              FROM done)
            SELECT count(*)::int FROM done
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("object_key", objectKey);
        command.Parameters.AddWithValue("width", width);
        command.Parameters.AddWithValue("height", height);
        return (int)(await command.ExecuteScalarAsync(ct))! == 1;
    }
}
