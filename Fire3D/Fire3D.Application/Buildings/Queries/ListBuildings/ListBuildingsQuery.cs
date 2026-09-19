using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.ListBuildings;

public sealed record ListBuildingsQuery(Guid ActorId, Guid OrganizationId, BuildingFilter Filter) : IRequest<AuthResult<PageResponse<BuildingSummaryResponse>>>;
