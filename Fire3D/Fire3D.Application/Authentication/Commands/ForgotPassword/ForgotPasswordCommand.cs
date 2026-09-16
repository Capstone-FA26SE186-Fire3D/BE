using MediatR;

namespace Fire3D.Application.Authentication.Commands.ForgotPassword;

public sealed record ForgotPasswordCommand(string Email) : IRequest;
