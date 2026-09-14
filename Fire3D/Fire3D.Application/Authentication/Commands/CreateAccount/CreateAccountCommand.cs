using MediatR;

namespace Fire3D.Application.Authentication.Commands.CreateAccount;

public sealed record CreateAccountCommand(Guid ActorId, CreateAccountRequest Request, Guid? CorrelationId = null) : IRequest<AuthResult<AccountResponse>>;
