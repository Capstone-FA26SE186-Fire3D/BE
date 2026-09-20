using Fire3D.Application.Ifc;
using Fire3D.Application.Administration;
namespace Fire3D.Infrastructure.Ifc;
public sealed partial class IfcReadStore
{
    public Task<PageResponse<BimFactResponse>?> ListBimFactsAsync(Guid revisionId, Guid? organizationId, int page, int pageSize, CancellationToken ct) =>
        ReadRevisionPageAsync<BimFactResponse>("""
            SELECT f.id,f.created_at,jsonb_build_object('id',f.id,'revisionId',f.revision_id,
                'ifcGlobalId',f.ifc_global_id,'entityType',f.entity_type,'propertyPath',f.property_path,
                'value',f.value,'sourceHash',f.source_hash,'qualityFlags',f.quality_flags,'createdAt',f.created_at) AS item
            FROM public.bim_facts f JOIN scoped_revision r ON r.id=f.revision_id
            """, revisionId, organizationId, page, pageSize, ct);
}
