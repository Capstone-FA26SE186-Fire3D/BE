using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Queries.ValidateSession;

internal sealed class ValidateSessionQueryHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<ValidateSessionQuery, bool>
{
    public async Task<bool> Handle(ValidateSessionQuery command, CancellationToken ct)
    {
        var userId = command.UserId;
        var familyId = command.FamilyId;
        var role = command.Role;
        var organizationId = command.OrganizationId;
        var user = await store.FindUserAsync(userId, ct);
        return user is not null && await AuthSupport.IsActiveAsync(store, user, ct)
            && user.Role.ToString() == role && user.OrganizationId?.ToString() == organizationId
            && await store.FamilyIsActiveAsync(userId, familyId, AuthSupport.UtcNow(clock), ct);
    }
}
