using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Administration.Queries.ListAccounts;

internal sealed class ListAccountsQueryHandler(IAdministrationStore store, IAuthStore auth)
    : IRequestHandler<ListAccountsQuery, AuthResult<PageResponse<ManagedAccountResponse>>>
{
    public async Task<AuthResult<PageResponse<ManagedAccountResponse>>> Handle(ListAccountsQuery query, CancellationToken ct)
    {
        if (!await AdministrationSupport.IsAdminAsync(auth, query.ActorId, ct))
            return AdministrationSupport.Forbidden<PageResponse<ManagedAccountResponse>>();
        var filter = query.Filter;
        if (!AdministrationSupport.ValidPage(filter.Page, filter.PageSize, filter.Search)
            || (filter.Role.HasValue && !Enum.IsDefined(filter.Role.Value)))
            return AdministrationSupport.Invalid<PageResponse<ManagedAccountResponse>>("Invalid filters. Page must be 1–100000, pageSize 1–100 and search at most 200 characters.");
        return AuthResult<PageResponse<ManagedAccountResponse>>.Ok(await store.ListAccountsAsync(filter, ct));
    }
}
