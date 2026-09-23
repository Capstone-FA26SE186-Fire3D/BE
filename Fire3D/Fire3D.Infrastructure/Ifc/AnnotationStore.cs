using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Ifc;

public sealed class AnnotationStore(Fire3DDbContext db) : IAnnotationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string SnapshotSql = """
        SELECT jsonb_build_object('revisionId',r.id,'id',a.id,'version',coalesce(a.version_number,0),
            'data',coalesce(a.data,'{"items":[]}'::jsonb),'provenance',a.provenance,
            'createdBy',a.created_by,'createdAt',a.created_at)::text
        FROM public.revisions r
        JOIN public.buildings b ON b.id=r.building_id
        JOIN public.organizations o ON o.id=b.organization_id
        LEFT JOIN LATERAL (SELECT * FROM public.annotation_sets WHERE revision_id=r.id
            ORDER BY version_number DESC LIMIT 1) a ON true
        WHERE r.id=@revision AND b.is_active AND b.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL
            AND (@tenant IS NULL OR b.organization_id=@tenant)
        """;
    private async Task<object?> Scalar(string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var (name,value) in args)
            if (name == "tenant") cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value ?? DBNull.Value });
            else cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct);
    }
    public async Task<AnnotationSnapshot?> ReadAsync(Guid revisionId, Guid? tenant, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var value = await Scalar(SnapshotSql,ct,("revision",revisionId),("tenant",tenant));
            return value is string json ? JsonSerializer.Deserialize<AnnotationSnapshot>(json,Json) : null;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
    public async Task<AuthResult<AnnotationSnapshot>> AppendAsync(Guid actorId, Guid revisionId, int expectedVersion, AnnotationData data, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Hold identity/tenant availability through commit. Lock the revision to serialize appenders.
        var tenant = await Scalar("""
            SELECT b.organization_id FROM public.revisions r
            JOIN public.buildings b ON b.id=r.building_id
            JOIN public.organizations o ON o.id=b.organization_id
            JOIN public.users u ON u.id=@actor
            WHERE r.id=@revision AND u.is_active AND u.deleted_at IS NULL
                AND b.is_active AND b.deleted_at IS NULL AND o.is_active AND o.deleted_at IS NULL
                AND ((u.role='PlatformAdmin' AND u.organization_id IS NULL)
                  OR (u.role='OrganizationUser' AND u.organization_id=b.organization_id))
            FOR SHARE OF u,o,b
            """,ct,("revision",revisionId),("actor",actorId));
        if (tenant is not Guid organizationId)
            return AuthResult<AnnotationSnapshot>.Fail("NOT_FOUND","Revision unavailable in this scope.",404);
        await Scalar("SELECT id FROM public.revisions WHERE id=@revision FOR UPDATE",ct,("revision",revisionId));
        var current = Convert.ToInt32(await Scalar("SELECT coalesce(max(version_number),0) FROM public.annotation_sets WHERE revision_id=@revision",ct,("revision",revisionId)));
        if (current != expectedVersion)
            return AuthResult<AnnotationSnapshot>.Fail("PRECONDITION_FAILED","Annotations changed. GET again before saving.",412);
        var anchors = data.Items.Select(x => x.IfcGlobalId).Distinct().ToArray();
        var found = Convert.ToInt32(await Scalar("SELECT count(DISTINCT ifc_global_id) FROM public.bim_facts WHERE revision_id=@revision AND ifc_global_id=ANY(@anchors)",
            ct,("revision",revisionId),("anchors",anchors)));
        if (found != anchors.Length)
            return AuthResult<AnnotationSnapshot>.Fail("INVALID_ANCHOR","Every IFC anchor must exist in this revision.",400);
        var id = Guid.NewGuid();
        await Scalar("""
            INSERT INTO public.annotation_sets(id,revision_id,version_number,data,provenance,created_by)
            VALUES (@id,@revision,@version,CAST(@data AS jsonb),'manual',@actor) RETURNING id
            """,ct,("id",id),("revision",revisionId),("version",checked(current+1)),("data",JsonSerializer.Serialize(data,Json)),("actor",actorId));
        await Scalar("""
            INSERT INTO public.audit_logs(user_id,organization_id,actor_type,action,target_entity,target_id,new_values)
            VALUES (@actor,@tenant,'User','Create','annotation_sets',@id,
                jsonb_build_object('revisionId',@revision,'version',@version)) RETURNING id
            """,ct,("actor",actorId),("tenant",organizationId),("id",id),("revision",revisionId),("version",current+1));
        var json = (string)(await Scalar(SnapshotSql,ct,("revision",revisionId),("tenant",organizationId)))!;
        var snapshot = JsonSerializer.Deserialize<AnnotationSnapshot>(json,Json)!;
        await tx.CommitAsync(ct);
        return AuthResult<AnnotationSnapshot>.Ok(snapshot);
    }
}
