using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.CreateBuilding;

public sealed record CreateBuildingCommand(Guid ActorId, Guid OrganizationId, CreateBuildingRequest Request) : IRequest<AuthResult<BuildingResponse>>;
