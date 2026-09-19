using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.GetRevision;

public sealed record GetRevisionQuery(Guid ActorId, Guid OrganizationId, Guid RevisionId) : IRequest<AuthResult<RevisionResponse>>;
