using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Administration.Queries.ListOrganizations;
public sealed record ListOrganizationsQuery(Guid ActorId, OrganizationFilter Filter)
    : IRequest<AuthResult<PageResponse<OrganizationResponse>>>;
