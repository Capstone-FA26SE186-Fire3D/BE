using System.Text.Json;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Ifc;

public sealed class EditorPreviewStore(Fire3DDbContext db) : IEditorPreviewStore
{
    public async Task<EditorPreviewSource?> ReadAsync(Guid buildingId, Guid revisionId, Guid? tenant, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT jsonb_build_object('buildingId', b.id, 'revisionId', r.id, 'revisionStatus', r.status,
                    'artifactId', x.id, 'attemptId', x.attempt_id, 'storageKey', x.storage_key,
                    'sha256Hash', x.sha256_hash, 'coordinateTransform', x.metadata->'coordinateTransform',
                    'floors', x.metadata->'floors', 'semanticMapping', x.metadata->'semanticMapping')::text
                FROM public.revisions r
                JOIN public.buildings b ON b.id=r.building_id
                JOIN public.organizations o ON o.id=b.organization_id
                LEFT JOIN LATERAL (
                    SELECT a.* FROM public.revision_artifacts a
                    JOIN public.processing_jobs j ON j.id=a.job_id AND j.revision_id=a.revision_id
                    JOIN public.processing_job_attempts t ON t.id=a.attempt_id AND t.processing_job_id=j.id
                        AND t.input_hash=j.input_hash
                    WHERE a.revision_id=r.id AND a.artifact_type='preview_glb'
                        AND j.kind='Geometry' AND j.status='Succeeded' AND t.status='Succeeded'
                        AND a.attempt_id=j.current_attempt_id
                    ORDER BY a.created_at DESC, a.id LIMIT 1
                ) x ON true
                WHERE r.id=@revision AND b.id=@building
                    AND b.is_active AND b.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL
                    AND (@tenant IS NULL OR b.organization_id=@tenant)
                """, (NpgsqlConnection)db.Database.GetDbConnection());
            cmd.Parameters.AddWithValue("revision", revisionId);
            cmd.Parameters.AddWithValue("building", buildingId);
            cmd.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)tenant ?? DBNull.Value });
            var json = await cmd.ExecuteScalarAsync(ct);
            return json is string value ? JsonSerializer.Deserialize<EditorPreviewSource>(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
