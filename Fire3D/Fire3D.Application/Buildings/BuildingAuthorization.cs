using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;

namespace Fire3D.Application.Buildings;

internal static class BuildingAuthorization
{
    // null is an explicit admin-wide read/resource lookup, never inferred from Guid.Empty.
    internal static async Task<AuthResult<Guid?>> ResolveScopeAsync(IAuthStore accounts, Guid actorId, Guid? requested, CancellationToken ct)
    {
        var actor = await accounts.FindUserAsync(actorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct))
            return AuthResult<Guid?>.Fail("FORBIDDEN", "An active account is required.", 403);
        if (requested == Guid.Empty) return AuthResult<Guid?>.Fail("VALIDATION_ERROR", "organizationId cannot be empty.", 400,
            new Dictionary<string, string[]> { ["organizationId"] = ["Choose a valid organizationId or omit it."] });
        if (actor.Role == UserRole.PlatformAdmin)
        {
            if (requested.HasValue && !await accounts.OrganizationIsActiveAsync(requested.Value, ct))
                return AuthResult<Guid?>.Fail("FORBIDDEN", "An active organization scope is required.", 403);
            return AuthResult<Guid?>.Ok(requested);
        }
        if (actor.Role != UserRole.OrganizationUser || !actor.OrganizationId.HasValue ||
            (requested.HasValue && requested != actor.OrganizationId))
            return AuthResult<Guid?>.Fail("FORBIDDEN", "An active organization scope is required.", 403);
        return AuthResult<Guid?>.Ok(actor.OrganizationId);
    }
    internal static async Task<bool> CanMutateAsync(IAuthStore accounts, Guid actorId, Guid organizationId, CancellationToken ct)
    {
        if (organizationId == Guid.Empty) return false;
        var actor = await accounts.FindUserAsync(actorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct)) return false;
        if (actor.Role == UserRole.PlatformAdmin) return await accounts.OrganizationIsActiveAsync(organizationId, ct);
        return actor.Role == UserRole.OrganizationUser && actor.OrganizationId == organizationId && await accounts.OrganizationIsActiveAsync(organizationId, ct);
    }
}
