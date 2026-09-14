using MediatR;

namespace Fire3D.Application.Authentication.Queries.GetCurrentAccount;

public sealed record GetCurrentAccountQuery(Guid UserId) : IRequest<AccountResponse?>;
