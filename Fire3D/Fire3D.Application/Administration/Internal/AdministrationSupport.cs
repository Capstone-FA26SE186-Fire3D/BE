using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;

namespace Fire3D.Application.Administration.Internal;

internal static class AdministrationSupport
{
    internal static async Task<bool> IsAdminAsync(IAuthStore store, Guid actorId, CancellationToken ct)
    {
        var actor = await store.FindUserAsync(actorId, ct);
        return actor is { IsActive: true, DeletedAt: null, Role: UserRole.PlatformAdmin, OrganizationId: null };
    }

    internal static bool ValidPage(int page, int size, string? search) =>
        page is >= 1 and <= 100000 && size is >= 1 and <= 100 && (search?.Length ?? 0) <= 200;
    internal static AuthResult<T> Forbidden<T>() => AuthResult<T>.Fail("FORBIDDEN", "PlatformAdmin is required.", 403);
    internal static AuthResult<T> NotFound<T>() => AuthResult<T>.Fail("NOT_FOUND", "Resource was not found.", 404);
    internal static AuthResult<T> Invalid<T>(string message) => AuthResult<T>.Fail("VALIDATION_ERROR", message, 400);
    internal static OrganizationResponse ToResponse(Organization x) =>
        new(x.Id, x.Name, x.Slug, x.IsActive, x.CreatedAt, x.UpdatedAt);
    internal static ManagedAccountResponse ToResponse(User x) =>
        new(x.Id, x.Email, x.FullName, x.Role, x.OrganizationId, x.IsActive, x.LastLoginAt, x.CreatedAt, x.UpdatedAt);
}
