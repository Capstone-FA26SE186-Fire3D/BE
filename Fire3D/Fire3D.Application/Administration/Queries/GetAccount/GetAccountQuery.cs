using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Administration.Queries.GetAccount;
public sealed record GetAccountQuery(Guid ActorId, Guid Id) : IRequest<AuthResult<ManagedAccountResponse>>;
