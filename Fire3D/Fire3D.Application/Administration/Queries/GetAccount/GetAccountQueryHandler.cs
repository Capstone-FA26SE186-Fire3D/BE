using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Administration.Queries.GetAccount;

internal sealed class GetAccountQueryHandler(IAuthStore auth)
    : IRequestHandler<GetAccountQuery, AuthResult<ManagedAccountResponse>>
{
    public async Task<AuthResult<ManagedAccountResponse>> Handle(GetAccountQuery query, CancellationToken ct)
    {
        if (!await AdministrationSupport.IsAdminAsync(auth, query.ActorId, ct))
            return AdministrationSupport.Forbidden<ManagedAccountResponse>();
        var target = await auth.FindUserAsync(query.Id, ct);
        return target is null || target.DeletedAt.HasValue
            ? AdministrationSupport.NotFound<ManagedAccountResponse>()
            : AuthResult<ManagedAccountResponse>.Ok(AdministrationSupport.ToResponse(target));
    }
}
