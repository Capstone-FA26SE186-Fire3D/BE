using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.ListRevisions;

public sealed record ListRevisionsQuery(Guid ActorId, Guid OrganizationId, Guid BuildingId) : IRequest<AuthResult<IReadOnlyList<RevisionResponse>>>;
