using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Administration.Queries.GetOrganization;
public sealed record GetOrganizationQuery(Guid ActorId, Guid Id) : IRequest<AuthResult<OrganizationResponse>>;
