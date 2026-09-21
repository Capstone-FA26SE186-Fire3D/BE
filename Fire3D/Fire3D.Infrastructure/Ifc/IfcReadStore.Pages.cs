using Fire3D.Application.Administration;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    private Task<PageResponse<T>?> ReadRevisionPageAsync<T>(string rowsSql, Guid revisionId, Guid? tenant,
        int page, int pageSize, CancellationToken ct) where T : class =>
        ReadJsonAsync<PageResponse<T>>("""
            WITH scoped_revision AS (
                SELECT r.id FROM public.revisions r
                JOIN public.buildings b ON b.id=r.building_id
                JOIN public.organizations o ON o.id=b.organization_id
                WHERE r.id=@id AND
            """ + " " + ScopePredicate + "), rows AS (" + rowsSql + ") " + """
            SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(p.item ORDER BY p.created_at DESC,p.id)
                FROM (SELECT * FROM rows ORDER BY created_at DESC,id LIMIT @limit OFFSET @offset) p),'[]'::jsonb),
                'totalCount',(SELECT count(*) FROM rows),'page',@page,'pageSize',@limit)::text
            FROM scoped_revision
            """,revisionId,tenant,ct,page,pageSize);
}
