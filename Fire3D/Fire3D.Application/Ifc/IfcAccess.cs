using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
namespace Fire3D.Application.Ifc;
public sealed record IfcScope(Guid? OrganizationId);
public static class IfcAccess
{
    public static async Task<AuthResult<IfcScope>> ResolveAsync(IAuthStore accounts, Guid actorId, CancellationToken ct)
    {
        var actor = await accounts.FindUserAsync(actorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct))
            return AuthResult<IfcScope>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        if (actor.Role is not (UserRole.OrganizationUser or UserRole.PlatformAdmin))
            return AuthResult<IfcScope>.Fail("FORBIDDEN", "Organization management access is required.", 403);
        return AuthResult<IfcScope>.Ok(new(actor.Role == UserRole.PlatformAdmin ? null : actor.OrganizationId));
    }
}
