using MediatR;

namespace Fire3D.Application.Authentication.Commands.Login;

public sealed record LoginCommand(LoginRequest Request) : IRequest<AuthResult<TokenResponse>>;
