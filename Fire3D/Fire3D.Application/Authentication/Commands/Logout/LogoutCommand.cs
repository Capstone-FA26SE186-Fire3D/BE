using MediatR;

namespace Fire3D.Application.Authentication.Commands.Logout;

public sealed record LogoutCommand(Guid UserId, Guid FamilyId) : IRequest;
