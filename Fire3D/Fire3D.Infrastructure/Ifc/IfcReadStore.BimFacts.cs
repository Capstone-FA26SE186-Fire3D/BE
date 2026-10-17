using Fire3D.Application.Ifc;
using Fire3D.Application.Administration;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    private const string BimFactScope = """
        WITH scoped_revision AS (
            SELECT r.id FROM public.revisions r
            JOIN public.buildings b ON b.id=r.building_id
            JOIN public.organizations o ON o.id=b.organization_id
            WHERE r.id=@id AND
        """;
    // BE#120: filters are parameters; qualityFlag selects every fact of an element that carries the flag on any of its facts
    // (SPACE_UNNAMED sits on the Name fact, the room's LongName/Category come with it).
    private const string BimFactFilter = """
        (@entityType IS NULL OR f.entity_type=@entityType) AND (@ifcGlobalId IS NULL OR f.ifc_global_id=@ifcGlobalId)
        AND (@qualityFlag IS NULL OR EXISTS(SELECT 1 FROM public.bim_facts g WHERE g.revision_id=f.revision_id
            AND g.ifc_global_id=f.ifc_global_id AND g.quality_flags ? @qualityFlag))
        """;

    private static Dictionary<string, string?> Filters(BimFactFilters filters) => new()
    {
        ["entityType"] = filters.EntityType, ["ifcGlobalId"] = filters.IfcGlobalId, ["qualityFlag"] = filters.QualityFlag
    };

    // Stable order (BE#120): the facts of one element are adjacent and never split by the previous created_at/random-id order.
    public Task<PageResponse<BimFactResponse>?> ListBimFactsAsync(Guid revisionId, Guid? organizationId, BimFactFilters filters, int page, int pageSize, CancellationToken ct) =>
        ReadJsonAsync<PageResponse<BimFactResponse>>(BimFactScope + " " + ScopePredicate + """
            ), rows AS (
              SELECT f.ifc_global_id,f.property_path,f.id,jsonb_build_object('id',f.id,'revisionId',f.revision_id,
                'ifcGlobalId',f.ifc_global_id,'entityType',f.entity_type,'propertyPath',f.property_path,
                'value',f.value,'sourceHash',f.source_hash,'qualityFlags',f.quality_flags,'createdAt',f.created_at) AS item
              FROM public.bim_facts f JOIN scoped_revision r ON r.id=f.revision_id
              WHERE
            """ + " " + BimFactFilter + """
            )
            SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(p.item ORDER BY p.ifc_global_id,p.property_path,p.id)
                FROM (SELECT * FROM rows ORDER BY ifc_global_id,property_path,id LIMIT @limit OFFSET @offset) p),'[]'::jsonb),
                'totalCount',(SELECT count(*) FROM rows),'page',@page,'pageSize',@limit)::text
            FROM scoped_revision
            """, revisionId, organizationId, ct, page, pageSize, Filters(filters));

    // One item per element (room), paged by element: Name, LongName, Category and the union of the quality flags of its facts.
    public Task<PageResponse<BimFactElementResponse>?> ListBimFactElementsAsync(Guid revisionId, Guid? organizationId, BimFactFilters filters, int page, int pageSize, CancellationToken ct) =>
        ReadJsonAsync<PageResponse<BimFactElementResponse>>(BimFactScope + " " + ScopePredicate + """
            ), rows AS (
              SELECT f.ifc_global_id,jsonb_build_object('ifcGlobalId',f.ifc_global_id,'entityType',min(f.entity_type),
                'name',(array_agg(f.value) FILTER(WHERE f.property_path='Name'))[1],
                'longName',(array_agg(f.value) FILTER(WHERE f.property_path='LongName'))[1],
                'category',(array_agg(f.value) FILTER(WHERE f.property_path='Category'))[1],
                'qualityFlags',COALESCE((SELECT jsonb_agg(DISTINCT q ORDER BY q) FROM public.bim_facts g CROSS JOIN LATERAL jsonb_array_elements_text(
                   CASE WHEN jsonb_typeof(g.quality_flags)='array' THEN g.quality_flags ELSE '[]'::jsonb END) q
                   WHERE g.revision_id=@id AND g.ifc_global_id=f.ifc_global_id),'[]'::jsonb),
                'sourceHash',min(f.source_hash),'updatedAt',max(f.created_at)) AS item
              FROM public.bim_facts f JOIN scoped_revision r ON r.id=f.revision_id
              WHERE
            """ + " " + BimFactFilter + """
              GROUP BY f.ifc_global_id
            )
            SELECT jsonb_build_object('items',COALESCE((SELECT jsonb_agg(p.item ORDER BY p.ifc_global_id)
                FROM (SELECT * FROM rows ORDER BY ifc_global_id LIMIT @limit OFFSET @offset) p),'[]'::jsonb),
                'totalCount',(SELECT count(*) FROM rows),'page',@page,'pageSize',@limit)::text
            FROM scoped_revision
            """, revisionId, organizationId, ct, page, pageSize, Filters(filters));
}
