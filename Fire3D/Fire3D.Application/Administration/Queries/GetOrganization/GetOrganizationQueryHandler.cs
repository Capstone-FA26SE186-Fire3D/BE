using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Administration.Queries.GetOrganization;

internal sealed class GetOrganizationQueryHandler(IAdministrationStore store, IAuthStore auth)
    : IRequestHandler<GetOrganizationQuery, AuthResult<OrganizationResponse>>
{
    public async Task<AuthResult<OrganizationResponse>> Handle(GetOrganizationQuery query, CancellationToken ct)
    {
        if (!await AdministrationSupport.IsAdminAsync(auth, query.ActorId, ct))
            return AdministrationSupport.Forbidden<OrganizationResponse>();
        var target = await store.GetOrganizationViewAsync(query.Id, ct);
        return target is null ? AdministrationSupport.NotFound<OrganizationResponse>() : AuthResult<OrganizationResponse>.Ok(target);
    }
}
