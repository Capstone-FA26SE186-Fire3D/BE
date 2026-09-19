using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;
namespace Fire3D.Application.Buildings.Queries.GetRevision;
public sealed class GetRevisionQueryHandler(IBuildingStore store, IAuthStore accounts)
    : IRequestHandler<GetRevisionQuery, AuthResult<RevisionResponse>>
{
    public async Task<AuthResult<RevisionResponse>> Handle(GetRevisionQuery query, CancellationToken ct)
    {
        var actor = await accounts.FindUserAsync(query.ActorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct))
            return AuthResult<RevisionResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        if (actor.Role is not (UserRole.PlatformAdmin or UserRole.OrganizationUser))
            return AuthResult<RevisionResponse>.Fail("FORBIDDEN", "Organization management access is required.", 403);
        if (query.RevisionId == Guid.Empty)
            return AuthResult<RevisionResponse>.Fail("VALIDATION_ERROR", "Revision ID is required.", 400);
        // Scope comes from the current database account, never a client claim/body.
        Guid? organizationId = actor.Role == UserRole.PlatformAdmin ? null : actor.OrganizationId;
        var result = await store.FindRevisionAsync(query.RevisionId, organizationId, ct);
        return result is null
            ? AuthResult<RevisionResponse>.Fail("NOT_FOUND", "Revision not found.", 404)
            : AuthResult<RevisionResponse>.Ok(result);
    }
}
