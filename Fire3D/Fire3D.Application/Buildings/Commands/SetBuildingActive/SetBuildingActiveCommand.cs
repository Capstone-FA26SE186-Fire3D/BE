using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.SetBuildingActive;

public sealed record SetBuildingActiveCommand(Guid ActorId, Guid OrganizationId, Guid BuildingId, bool IsActive) : IRequest<AuthResult<BuildingSummaryResponse>>;
