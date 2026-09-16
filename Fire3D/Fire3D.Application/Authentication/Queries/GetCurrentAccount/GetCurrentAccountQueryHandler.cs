using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Queries.GetCurrentAccount;

internal sealed class GetCurrentAccountQueryHandler(IAuthStore store)
    : IRequestHandler<GetCurrentAccountQuery, AccountResponse?>
{
    public async Task<AccountResponse?> Handle(GetCurrentAccountQuery command, CancellationToken ct)
    {
        var userId = command.UserId;
        var user = await store.FindUserAsync(userId, ct);
        return user is not null && await AuthSupport.IsActiveAsync(store, user, ct) ? AuthSupport.ToAccount(user) : null;
    }
}
