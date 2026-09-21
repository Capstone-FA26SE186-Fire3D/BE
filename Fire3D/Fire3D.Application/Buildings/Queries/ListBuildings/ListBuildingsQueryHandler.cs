using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.ListBuildings;

internal sealed class ListBuildingsQueryHandler(IBuildingStore store)
    : IRequestHandler<ListBuildingsQuery, AuthResult<PageResponse<BuildingSummaryResponse>>>
{
    public async Task<AuthResult<PageResponse<BuildingSummaryResponse>>> Handle(ListBuildingsQuery query, CancellationToken ct)
    {
        if (query.Filter.Page < 1 || query.Filter.Page > 100000 || query.Filter.PageSize < 1 || query.Filter.PageSize > 100 || query.Filter.Search?.Length > 200)
            return AuthResult<PageResponse<BuildingSummaryResponse>>.Fail("VALIDATION_ERROR", "Invalid filters. Page 1-100000, pageSize 1-100, search max 200 chars.", 400);

        var result = await store.ListBuildingsAsync(query.OrganizationId, query.Filter, ct);
        return AuthResult<PageResponse<BuildingSummaryResponse>>.Ok(result);
    }
}
