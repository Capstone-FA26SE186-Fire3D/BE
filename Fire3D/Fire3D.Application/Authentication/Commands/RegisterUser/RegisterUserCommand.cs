using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

/// <summary>Legacy command retained for callers that have not moved to RegisterTraineeCommand.</summary>
public sealed record RegisterUserCommand(string Email, string Password, string FullName, string? Username = null, string? ConfirmPassword = null,
    DateOnly? Dob = null, UserGender? Gender = null, string? PhoneNumber = null, string? AvatarUrl = null)
    : IRequest<AuthResult<AccountResponse>>;

public sealed class RegisterUserCommandHandler(IAuthStore store, IPasswordService passwords,
    IEmailVerificationQueue verificationQueue, TimeProvider clock)
    : IRequestHandler<RegisterUserCommand, AuthResult<AccountResponse>>
{
    public Task<AuthResult<AccountResponse>> Handle(RegisterUserCommand command, CancellationToken ct) =>
        new RegisterTraineeCommandHandler(store, passwords, verificationQueue, clock).Handle(
            new RegisterTraineeCommand(command.Email, command.Username ?? string.Empty, command.Password,
                command.ConfirmPassword ?? string.Empty, command.FullName, command.Dob, command.Gender, command.PhoneNumber), ct);
}
