using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Application.Administration;
using Fire3D.Domain.Enums;
using MediatR;
namespace Fire3D.Application.Buildings.Queries.ListRevisions;
public sealed class ListRevisionsQueryHandler(IBuildingStore store, IAuthStore accounts)
    : IRequestHandler<ListRevisionsQuery, AuthResult<PageResponse<RevisionResponse>>>
{
    public async Task<AuthResult<PageResponse<RevisionResponse>>> Handle(ListRevisionsQuery query, CancellationToken ct)
    {
        var actor = await accounts.FindUserAsync(query.ActorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct))
            return AuthResult<PageResponse<RevisionResponse>>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        if (actor.Role is not (UserRole.OrganizationUser or UserRole.PlatformAdmin))
            return AuthResult<PageResponse<RevisionResponse>>.Fail("FORBIDDEN", "Organization management access is required.", 403);
        if (query.BuildingId == Guid.Empty || query.Page is < 1 or > 100000 || query.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<RevisionResponse>>.Fail("VALIDATION_ERROR", "Building ID, page 1-100000 and pageSize 1-100 are required.", 400);
        Guid? tenant = actor.Role == UserRole.PlatformAdmin ? null : actor.OrganizationId;
        if (!await store.RevisionBuildingExistsAsync(query.BuildingId, tenant, ct))
            return AuthResult<PageResponse<RevisionResponse>>.Fail("NOT_FOUND", "Building not found.", 404);
        return AuthResult<PageResponse<RevisionResponse>>.Ok(await store.ListRevisionsAsync(query.BuildingId, tenant, query.Page, query.PageSize, ct));
    }
}
