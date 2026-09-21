using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.UpdateBuilding;

public sealed record UpdateBuildingCommand(Guid ActorId, Guid OrganizationId, Guid BuildingId, UpdateBuildingRequest Request) : IRequest<AuthResult<BuildingResponse>>;
