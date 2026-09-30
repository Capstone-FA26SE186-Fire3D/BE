using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;

namespace Fire3D.Application.Buildings;

internal static class BuildingAuthorization
{
    internal static async Task<bool> CanMutateAsync(IAuthStore accounts, Guid actorId, Guid organizationId, CancellationToken ct)
    {
        if (organizationId == Guid.Empty) return false;
        var actor = await accounts.FindUserAsync(actorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct)) return false;
        if (actor.Role == UserRole.PlatformAdmin) return await accounts.OrganizationIsActiveAsync(organizationId, ct);
        return actor.Role == UserRole.OrganizationUser && actor.OrganizationId == organizationId && await accounts.OrganizationIsActiveAsync(organizationId, ct);
    }
}
