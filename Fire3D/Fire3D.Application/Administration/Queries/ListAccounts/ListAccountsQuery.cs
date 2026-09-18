using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Administration.Queries.ListAccounts;
public sealed record ListAccountsQuery(Guid ActorId, AccountFilter Filter)
    : IRequest<AuthResult<PageResponse<ManagedAccountResponse>>>;
