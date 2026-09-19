using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.GetRevision;

internal sealed class GetRevisionQueryHandler(IBuildingStore store)
    : IRequestHandler<GetRevisionQuery, AuthResult<RevisionResponse>>
{
    public async Task<AuthResult<RevisionResponse>> Handle(GetRevisionQuery query, CancellationToken ct)
    {
        var result = await store.FindRevisionAsync(query.RevisionId, query.OrganizationId, ct);
        if (result == null)
            return AuthResult<RevisionResponse>.Fail("NOT_FOUND", "Revision not found.", 404);

        return AuthResult<RevisionResponse>.Ok(result);
    }
}
