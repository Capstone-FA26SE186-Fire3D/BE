using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Administration.Queries.ListOrganizations;

internal sealed class ListOrganizationsQueryHandler(IAdministrationStore store, IAuthStore auth)
    : IRequestHandler<ListOrganizationsQuery, AuthResult<PageResponse<OrganizationResponse>>>
{
    public async Task<AuthResult<PageResponse<OrganizationResponse>>> Handle(ListOrganizationsQuery query, CancellationToken ct)
    {
        if (!await AdministrationSupport.IsAdminAsync(auth, query.ActorId, ct))
            return AdministrationSupport.Forbidden<PageResponse<OrganizationResponse>>();
        var filter = query.Filter;
        if (!AdministrationSupport.ValidPage(filter.Page, filter.PageSize, filter.Search))
            return AdministrationSupport.Invalid<PageResponse<OrganizationResponse>>("Invalid filters. Page must be 1–100000, pageSize 1–100 and search at most 200 characters.");
        return AuthResult<PageResponse<OrganizationResponse>>.Ok(await store.ListOrganizationsAsync(filter, ct));
    }
}
