using MediatR;

namespace Fire3D.Application.Authentication.Commands.RefreshToken;

public sealed record RefreshTokenCommand(string? RefreshToken) : IRequest<AuthResult<TokenResponse>>;
