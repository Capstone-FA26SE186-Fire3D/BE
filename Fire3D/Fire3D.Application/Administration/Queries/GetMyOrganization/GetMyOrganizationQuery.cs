using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Administration.Queries.GetMyOrganization;

public sealed record GetMyOrganizationQuery(Guid ActorId) : IRequest<AuthResult<OrganizationResponse>>;

public sealed class GetMyOrganizationQueryHandler(IAuthStore accounts, IAdministrationStore organizations)
    : IRequestHandler<GetMyOrganizationQuery, AuthResult<OrganizationResponse>>
{
    public async Task<AuthResult<OrganizationResponse>> Handle(GetMyOrganizationQuery request, CancellationToken ct)
    {
        var actor = await accounts.FindUserAsync(request.ActorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct))
            return AuthResult<OrganizationResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        if (actor.Role != UserRole.OrganizationUser || !actor.OrganizationId.HasValue)
            return AuthResult<OrganizationResponse>.Fail("FORBIDDEN", "Organization membership is required.", 403);

        var organization = await organizations.FindOrganizationAsync(actor.OrganizationId.Value, ct);
        return organization is null || !organization.IsActive || organization.DeletedAt.HasValue
            ? AuthResult<OrganizationResponse>.Fail("UNAUTHORIZED", "Organization is unavailable.", 401)
            : AuthResult<OrganizationResponse>.Ok(new OrganizationResponse(organization.Id, organization.Name,
                organization.Slug, organization.IsActive, organization.CreatedAt, organization.UpdatedAt));
    }
}
