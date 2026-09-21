using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.GetBuilding;

public sealed record GetBuildingQuery(Guid ActorId, Guid OrganizationId, Guid BuildingId) : IRequest<AuthResult<BuildingResponse>>;
