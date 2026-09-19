using MediatR;

namespace Fire3D.Application.Authentication.Commands.Register;

public sealed record RegisterCommand(RegisterRequest Request) : IRequest<AuthResult<TokenResponse>>;
