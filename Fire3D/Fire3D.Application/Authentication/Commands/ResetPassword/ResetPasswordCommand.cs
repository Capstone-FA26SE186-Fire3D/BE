using MediatR;

namespace Fire3D.Application.Authentication.Commands.ResetPassword;

public sealed record ResetPasswordCommand(Guid Token, string NewPassword) : IRequest<AuthResult<bool>>;
