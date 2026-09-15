using MediatR;

namespace Fire3D.Application.Authentication.Commands.BootstrapAdmin;

public sealed record BootstrapAdminCommand(string Email, string Password) : IRequest<AuthResult<AccountResponse>>;
