using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Fire3D.API.Authorization;

/// <summary>
/// Reads identity values guaranteed by the JWT authentication handler after it has validated the session.
/// Controllers must only call these methods from actions protected by <c>[Authorize]</c>.
/// </summary>
public static class CurrentActorExtensions
{
    public static Guid GetActorId(this ClaimsPrincipal principal) =>
        GetRequiredGuid(principal, JwtRegisteredClaimNames.Sub);

    public static Guid GetSessionFamilyId(this ClaimsPrincipal principal) =>
        GetRequiredGuid(principal, "sid");

    public static Guid? GetOrganizationId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue("organization_id");
        return Guid.TryParse(value, out var organizationId) ? organizationId : null;
    }

    private static Guid GetRequiredGuid(ClaimsPrincipal principal, string claimType) =>
        Guid.TryParse(principal.FindFirstValue(claimType), out var value)
            ? value
            : throw new InvalidOperationException($"Authenticated principal is missing a valid {claimType} claim.");
}
