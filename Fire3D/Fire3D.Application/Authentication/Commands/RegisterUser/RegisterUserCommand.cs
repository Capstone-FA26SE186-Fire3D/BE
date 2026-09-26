using Fire3D.Application.Authentication.Commands.SelfRegistration;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

/// <summary>Legacy command retained for callers that have not moved to RegisterTraineeCommand.</summary>
public sealed record RegisterUserCommand(string Email, string Password, string FullName, string? Username = null, string? ConfirmPassword = null)
    : IRequest<AuthResult<AccountResponse>>;

public sealed class RegisterUserCommandHandler(IAuthStore store, IPasswordService passwords, TimeProvider clock)
    : IRequestHandler<RegisterUserCommand, AuthResult<AccountResponse>>
{
    public Task<AuthResult<AccountResponse>> Handle(RegisterUserCommand command, CancellationToken ct) =>
        new RegisterTraineeCommandHandler(store, passwords, clock).Handle(
            new RegisterTraineeCommand(command.Email, command.Username ?? string.Empty, command.Password,
                command.ConfirmPassword ?? string.Empty, command.FullName), ct);
}
