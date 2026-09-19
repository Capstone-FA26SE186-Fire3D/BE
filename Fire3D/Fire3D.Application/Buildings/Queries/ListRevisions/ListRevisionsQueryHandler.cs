using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.ListRevisions;

internal sealed class ListRevisionsQueryHandler(IBuildingStore store)
    : IRequestHandler<ListRevisionsQuery, AuthResult<IReadOnlyList<RevisionResponse>>>
{
    public async Task<AuthResult<IReadOnlyList<RevisionResponse>>> Handle(ListRevisionsQuery query, CancellationToken ct)
    {
        // Optional: Ensure building exists and belongs to the org
        var building = await store.FindBuildingAsync(query.BuildingId, query.OrganizationId, ct);
        if (building == null)
            return AuthResult<IReadOnlyList<RevisionResponse>>.Fail("NOT_FOUND", "Building not found.", 404);

        var result = await store.ListRevisionsAsync(query.BuildingId, query.OrganizationId, ct);
        return AuthResult<IReadOnlyList<RevisionResponse>>.Ok(result);
    }
}
